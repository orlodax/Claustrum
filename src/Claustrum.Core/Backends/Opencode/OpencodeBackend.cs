using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Opencode;

// docs/PLAN.md §A3 "opencode" row, measured against a real opencode **2.0.12** install on
// 2026-09-22 — NOTES.md "The opencode and api backends, validated against real endpoints" is the
// record of every flag, event name and field below, and of the four things v2 took away that the
// 2026-09-18 v1.18.31 pass had confirmed (`--dir`, `--variant`, `OPENCODE_PERMISSION`, and a `run`
// that could reach the shared background service with this process's environment).
public sealed class OpencodeBackend(IPlatform platform) : IBackend, IPostRunHook
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    public string Name => "opencode";

    public IStreamFormatter? CreateStreamFormatter(string cwd) => new OpencodeStreamFormatter(cwd);

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(Name, ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        string agentName = $"claustrum-{run.Role.Name}";
        string configContent = BuildConfigContent(agentName, run.SystemPromptFilePath, run.Role.Permission);

        // Two departures from docs/PLAN.md §A3's opencode argv, both measured 2026-09-22 — NOTES.md
        // "The opencode and api backends, validated against real endpoints" has the evidence.
        // `--standalone`: otherwise the shared `opencode serve --service` daemon runs the job and
        // never sees this process's OPENCODE_CONFIG_CONTENT (`Agent not found`). `--auto` at every
        // level, not just Full: without it a headless run stalls on a prompt nobody can answer, and
        // explicit denies survive it — which is the whole reason WritePermission turns opencode's
        // own `ask` defaults into denies. v2 also dropped `--dir`; ProcessRunner's WorkingDirectory
        // is what puts the run in run.Cwd now. `--model` is the resolved model as is: a `#<variant>` rides
        // only when the model spec itself carries one, never from the tier's effort (#35, NOTES.md).
        List<string> args = ["run", "--standalone", "--agent", agentName, "--model", run.Role.Model, "--format", "json", "--auto"];
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--session", resumeSession]);
        args.Add(run.Brief);

        Dictionary<string, string> env = new(run.Env, StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG_CONTENT"] = configContent,

            // A one-shot headless run has no UI to live-update, and the watcher costs an inotify
            // instance: at the host's `fs.inotify.max_user_instances` the server stops dead after
            // its "watcher subscribe" log line and never answers (measured 2026-09-22).
            ["OPENCODE_DISABLE_FILEWATCHER"] = "1",
        };

        return new ProcessSpec(Name, [.. args], run.Cwd, env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        StepScan scan = Scan(trimmed);

        // A reported opencode cost is complete or absent, never partial: `step_finish` is emitted
        // *before* each tool call and never after the final message, so a stream whose steps do not
        // all report back is a floor, not a total (NOTES.md "The opencode and api backends, validated
        // against real endpoints", defect 3). Charging a floor verbatim would silently under-bill the
        // §D4 ledger; leaving both null makes Runner.ChargeAsync charge the cap and say so.
        decimal? cost = null;
        Usage? usage = null;
        if (scan.StepFinishes.Count == scan.StepStarts.Count)
            foreach (JsonElement finished in scan.StepFinishes.Values)
                AddStepFinish(finished, ref cost, ref usage);

        string finalMessage = string.Join("", scan.TextParts.OrderBy(entry => entry.Value.Order).Select(entry => entry.Value.Text));

        // A `type:"error"` event is the run failing, whatever the exit code says: opencode is not
        // contractually bound to also exit non-zero, and trusting the exit code alone reported a
        // failed run as Success with the error text sitting in final_message (review finding). The
        // message is appended rather than only used as a fallback so a run that produced partial
        // text before failing does not silently drop the reason.
        if (scan.ErrorMessage is not null)
            finalMessage = finalMessage.Length == 0 ? scan.ErrorMessage : $"{finalMessage}\n\n{scan.ErrorMessage}";

        return new ParsedOutput(finalMessage, scan.SessionId, cost, usage, [], scan.LastEvent, IsError: exitCode != 0 || scan.ErrorMessage is not null);
    }

    private sealed record StepScan(
        string? SessionId,
        Dictionary<string, (int Order, string Text)> TextParts,
        Dictionary<string, JsonElement> StepFinishes,
        List<string> StepStarts,
        bool ToolAfterLastStart,
        bool FinishAfterLastStart,
        string? ErrorMessage,
        JsonElement? LastEvent);

    private static StepScan Scan(string trimmed)
    {
        string? sessionId = null;
        Dictionary<string, (int Order, string Text)> textParts = [];
        Dictionary<string, JsonElement> stepFinishes = [];
        List<string> stepStarts = [];
        bool toolAfterLastStart = false;
        bool finishAfterLastStart = false;
        string? errorMessage = null;
        JsonElement? lastEvent = null;
        int nextOrder = 0;

        foreach (string rawLine in trimmed.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                lastEvent = root.Clone();
                if (GetString(root, "sessionID") is { } eventSession)
                    sessionId = eventSession;

                string? type = GetString(root, "type");
                if (type == "error")
                {
                    errorMessage = ExtractErrorMessage(root, line);
                    continue;
                }

                // opencode 2.0.12's `run --format json`, captured 2026-09-22: the event names are
                // `text`/`step_finish`/`tool_use`/`step_start`, each wrapping the part the SDK
                // documents (`part.type` is its kebab-case twin). Nothing resembling v1's
                // `message.part.updated` is emitted; only one `text` event per part was seen, and it
                // carries the whole text, so a repeat for the same id replaces rather than appends —
                // which is why every handler here keys on `part.id`, a `step_finish` no less than a
                // `text` (a repeated one would otherwise be billed twice).
                if (!root.TryGetProperty("part", out JsonElement part) || part.ValueKind != JsonValueKind.Object || GetString(part, "id") is not { } partId)
                    continue;

                if (type == "text" && GetString(part, "text") is { } text)
                {
                    int order = textParts.TryGetValue(partId, out (int Order, string Text) existing) ? existing.Order : nextOrder++;
                    textParts[partId] = (order, text);
                }
                else if (type == "step_start")
                {
                    if (!stepStarts.Contains(partId))
                    {
                        stepStarts.Add(partId);
                        toolAfterLastStart = false;
                        finishAfterLastStart = false;
                    }
                }
                else if (type == "tool_use")
                {
                    toolAfterLastStart = true;
                }
                else if (type == "step_finish")
                {
                    stepFinishes[partId] = part.Clone();
                    finishAfterLastStart = true;
                }
            }
        }

        return new StepScan(sessionId, textParts, stepFinishes, stepStarts, toolAfterLastStart, finishAfterLastStart, errorMessage, lastEvent);
    }

    // Two genuinely captured shapes, one per major version, so both are read: v2.0.12 writes
    // `error: {type, message}` (2026-09-22), v1.18.31 wrote `error: {name, data: {message, ref}}`
    // (2026-09-18, tests/fixtures/opencode/error-v1.jsonl). Neither is a guess.
    private static string ExtractErrorMessage(JsonElement root, string rawLine)
    {
        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
            return rawLine;

        if (error.TryGetProperty("message", out JsonElement flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString() ?? rawLine;

        return error.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && GetString(data, "message") is { } nested
                ? nested
                : rawLine;
    }

    // Each step's numbers are its own, not a running total, so they add up: the captured
    // `cost: 0.000523614` is exactly `10526 × $0.049/M + (60 + 20) × $0.098/M` for that one step's
    // own tokens at deepseek-v4-flash's published price. Overwriting (what this did) reported only
    // the last step of a multi-step run.
    private static void AddStepFinish(JsonElement part, ref decimal? cost, ref Usage? usage)
    {
        if (part.TryGetProperty("cost", out JsonElement costProp) && costProp.ValueKind == JsonValueKind.Number && costProp.TryGetDecimal(out decimal costValue))
            cost = (cost ?? 0m) + costValue;

        if (!part.TryGetProperty("tokens", out JsonElement tokens) || tokens.ValueKind != JsonValueKind.Object)
            return;

        JsonElement cache = tokens.TryGetProperty("cache", out JsonElement cacheProp) ? cacheProp : default;
        usage = new Usage(
            Add(usage?.InputTokens, TryGetInt(tokens, "input")),
            Add(usage?.OutputTokens, TryGetInt(tokens, "output")),
            Add(usage?.CacheReadInputTokens, TryGetInt(cache, "read")),
            Add(usage?.CacheCreationInputTokens, TryGetInt(cache, "write")));
    }

    // Null stays null when neither side has a number: "opencode did not report this" and "it
    // reported zero" are different answers, and RunResult prints both.
    private static int? Add(int? running, int? step) =>
        running is null && step is null ? null : (running ?? 0) + (step ?? 0);

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out int value) ? value : null;

    // IPostRunHook: the real cost is `opencode session export`'s `info.cost`, because the final step
    // never emits a step_finish (defect 3). The export is the session's running total, so a resumed
    // run is charged after − before. Best-effort and never fatal: NOTES.md "The opencode and api backends,
    // validated against real endpoints" (#125 bullets) has the resume bill, the truncation and the fallback.
    async Task<PreRunBaseline?> IPostRunHook.PreRunAsync(PostRunContext context, CancellationToken cancellationToken)
    {
        if (context.ResumeSession is not { Length: > 0 } sessionId)
            return null;

        SessionExport before = await ExportAsync(sessionId, context, cancellationToken);
        return new PreRunBaseline(before.CostUsd, before.Usage, before.Failure);
    }

    async Task<PostRunResult?> IPostRunHook.PostRunAsync(
        ParsedOutput parsed,
        PostRunContext context,
        PreRunBaseline? baseline,
        CancellationToken cancellationToken)
    {
        if (parsed.SessionId is not { Length: > 0 } sessionId)
            return null;

        // A baseline only applies when the run really continued that session.
        bool resumed = baseline is not null && sessionId == context.ResumeSession;
        if (resumed && baseline!.Failure is { } baselineFailure)
            return FailedExport(parsed, context.Stdout, $"the pre-run export of the resumed session failed: {baselineFailure}");

        SessionExport after = await ExportAsync(sessionId, context, cancellationToken);
        if (after.Failure is { } failure)
            return FailedExport(parsed, context.Stdout, failure);

        if (!resumed)
            return new PostRunResult(after.CostUsd, after.Usage, Warning: null);

        decimal cost = after.CostUsd!.Value - (baseline!.CostUsd ?? 0m);
        Usage? usage = SubtractUsage(after.Usage, baseline.Usage);
        return cost < 0m || (after.Usage is not null && usage is null)
            ? FailedExport(parsed, context.Stdout, "the resumed session's total went down between the two exports")
            : new PostRunResult(cost, usage, Warning: null);
    }

    // Null when any field would go negative: a total that shrank is a bad reading, not a refund.
    private static Usage? SubtractUsage(Usage? after, Usage? before)
    {
        if (after is null)
            return null;

        int?[] fields =
        [
            Delta(after.InputTokens, before?.InputTokens),
            Delta(after.OutputTokens, before?.OutputTokens),
            Delta(after.CacheReadInputTokens, before?.CacheReadInputTokens),
            Delta(after.CacheCreationInputTokens, before?.CacheCreationInputTokens),
        ];

        return fields.Any(field => field < 0) ? null : new Usage(fields[0], fields[1], fields[2], fields[3]);
    }

    private static int? Delta(int? after, int? before) => after is null ? null : after.Value - (before ?? 0);

    private sealed record SessionExport(decimal? CostUsd, Usage? Usage, string? Failure, bool Unparseable = false);

    private static readonly TimeSpan exportTimeout = TimeSpan.FromSeconds(30);

    // One retry, and only for JSON that did not parse: a cut-off export is the one failure a second
    // attempt can cure; a missing binary or a non-zero exit would fail the same way again.
    private async Task<SessionExport> ExportAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        SessionExport export = await ExportOnceAsync(sessionId, context, cancellationToken);
        return export.Unparseable ? await ExportOnceAsync(sessionId, context, cancellationToken) : export;
    }

    private async Task<SessionExport> ExportOnceAsync(string sessionId, PostRunContext context, CancellationToken cancellationToken)
    {
        // On POSIX the export goes to a file in the job directory through a one-line `sh` redirect: opencode
        // exits before a full stdout pipe drains, and a reader that is merely slow gets a cut-off document
        // (measured 2026-10-15: 8 192 bytes of 1.27 MB behind a 3 s reader). A file write completes first.
        string? outputPath = platform.Os == ClaustrumOs.Windows ? null : Path.Combine(context.JobDirectory, $"session-export-{Guid.NewGuid():N}.tmp");
        try
        {
            ResolvedBinary binary = BinaryLocator.Locate("opencode", ["session", "export", "--standalone", sessionId], context.BackendConfig, platform);

            ProcessStartInfo startInfo = new()
            {
                FileName = outputPath is null ? binary.Executable : "/bin/sh",
                WorkingDirectory = context.Spec.Cwd,
                UseShellExecute = false,
                RedirectStandardOutput = outputPath is null,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            string[] argv = outputPath is null
                ? binary.Args
                : ["-c", "out=$1; shift; exec \"$@\" > \"$out\"", "sh", outputPath, binary.Executable, .. binary.Args];
            foreach (string arg in argv)
                startInfo.ArgumentList.Add(arg);

            // The run's own environment, `--env` entries and `env_passthrough: all` included.
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string> entry in EnvAllowList.Build(platform, context.Spec.Env, context.EnvPassthroughAll))
                startInfo.Environment[entry.Key] = entry.Value;

            using System.Diagnostics.Process process = new() { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();

            Task<string>? stdoutTask = outputPath is null ? process.StandardOutput.ReadToEndAsync(CancellationToken.None) : null;
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using CancellationTokenSource timeoutSource = new(exportTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            string stderr;
            string json;
            try
            {
                await process.WaitForExitAsync(linked.Token);

                // The reads share the deadline: a grandchild that inherited the pipe (an MCP/LSP server
                // from the run's config) outlives the export and would hold ReadToEnd open forever.
                stderr = await stderrTask.WaitAsync(linked.Token);
                if (process.ExitCode != 0)
                {
                    string detail = stderr.Trim();
                    return new SessionExport(null, null, detail.Length > 0 ? detail[..Math.Min(detail.Length, 300)] : $"exit code {process.ExitCode}");
                }

                json = stdoutTask is not null ? await stdoutTask.WaitAsync(linked.Token) : await File.ReadAllTextAsync(outputPath!, linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(CancellationToken.None);
                return new SessionExport(null, null, timeoutSource.IsCancellationRequested ? "timed out after 30s" : "cancelled");
            }

            return ReadTotals(json);
        }
        catch (Exception ex)
        {
            return new SessionExport(null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (outputPath is not null)
                try { File.Delete(outputPath); } catch (IOException) { }
        }
    }

    // Only `info.cost` and `info.tokens` matter; the messages (~1 MB for a long session) are ignored.
    private static SessionExport ReadTotals(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out JsonElement info) || info.ValueKind != JsonValueKind.Object)
                return new SessionExport(null, null, "export JSON has no 'info' object");

            if (!info.TryGetProperty("cost", out JsonElement costProp) || costProp.ValueKind != JsonValueKind.Number || !costProp.TryGetDecimal(out decimal cost))
                return new SessionExport(null, null, "export info has no cost");

            Usage? usage = null;
            if (info.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                // Same field layout as AddStepFinish; reasoning tokens are dropped (Usage has no field for them).
                JsonElement cache = tokens.TryGetProperty("cache", out JsonElement cacheProp) && cacheProp.ValueKind == JsonValueKind.Object
                    ? cacheProp
                    : default;
                usage = new Usage(TryGetInt(tokens, "input"), TryGetInt(tokens, "output"), TryGetInt(cache, "read"), TryGetInt(cache, "write"));
            }

            return new SessionExport(cost, usage, null);
        }
        catch (JsonException ex)
        {
            return new SessionExport(null, null, $"unparseable export JSON: {ex.Message}", Unparseable: true);
        }
    }

    // #125 fallback: step costs when they are complete, else the step sum when the only gap is the
    // final text-only step (it never emits a step_finish), else null and the cap.
    private static PostRunResult FailedExport(ParsedOutput parsed, string stdout, string reason)
    {
        string prefix = $"opencode session export failed ({reason})";
        if (parsed.CostUsd is not null)
            return new PostRunResult(null, null, $"{prefix}: cost from step_finish events");

        (decimal? Cost, Usage? Usage)? sum = stdout.Trim() is { Length: > 0 } trimmed ? SumWithoutFinalTextStep(Scan(trimmed)) : null;

        // The words follow the outcome: a lone text-only step sums to (null, null), which is no step sum at all.
        return sum is { Cost: not null }
            ? new PostRunResult(sum.Value.Cost, sum.Value.Usage, $"{prefix}: cost is the step sum, without the final text step")
            : new PostRunResult(null, sum?.Usage, $"{prefix}: cost unknown, charged the cap");
    }

    private static (decimal? Cost, Usage? Usage)? SumWithoutFinalTextStep(StepScan scan)
    {
        // A step_finish has its own part id, so steps pair up by order, not by id: one more start than
        // finishes, with nothing finished or called since the last start, means only the final step is open.
        if (scan.StepStarts.Count != scan.StepFinishes.Count + 1 || scan.FinishAfterLastStart || scan.ToolAfterLastStart)
            return null;

        decimal? cost = null;
        Usage? usage = null;
        foreach (JsonElement finished in scan.StepFinishes.Values)
            AddStepFinish(finished, ref cost, ref usage);

        return (cost, usage);
    }

    // `{file:<path>}` substitution inside OPENCODE_CONFIG_CONTENT is real (confirmed live): opencode
    // reads the referenced file's contents in place of the placeholder, so the rendered role body
    // never has to be JSON-escaped by hand the way an inline string would. The permission object is
    // written twice — on the agent, as docs/PLAN.md §A3's opencode row has it, *and* at the top
    // level, because the agent copy binds only the primary agent: a `readonly` run handed the job
    // to opencode's own `subagent` tool, which wrote the file through `general` (measured
    // 2026-09-22). v2 removed the OPENCODE_PERMISSION env var v1.18.31 read all this from.
    private static string BuildConfigContent(string agentName, string systemPromptFilePath, PermissionPolicy permission)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("agent");
            writer.WriteStartObject(agentName);
            writer.WriteString("mode", "primary");
            writer.WriteString("prompt", $"{{file:{systemPromptFilePath}}}");
            WritePermission(writer, permission);
            writer.WriteEndObject();
            writer.WriteEndObject();
            WritePermission(writer, permission);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // docs/PLAN.md §A3 permission table's opencode column, plus `execute`: v2 ships a second
    // command-running tool under that name (a restricted JS sandbox), and a `readonly` run reached
    // for it thirty times when `bash` was denied (measured 2026-09-22). It got nowhere — the sandbox
    // has no fs, no `process`, no network — but "bash denied" must not mean "the other one is open".
    private static void WritePermission(Utf8JsonWriter writer, PermissionPolicy permission)
    {
        writer.WriteStartObject("permission");
        switch (permission.Level)
        {
            case PermissionLevel.ReadOnly:
                writer.WriteString("edit", "deny");
                writer.WriteString("bash", "deny");
                writer.WriteString("execute", "deny");
                break;
            case PermissionLevel.Shell:
                writer.WriteString("edit", "deny");
                WriteBashRules(writer, permission.Deny);
                break;
            case PermissionLevel.Edit:
                writer.WriteString("edit", "allow");
                writer.WriteString("bash", "deny");
                writer.WriteString("execute", "deny");
                break;
            case PermissionLevel.EditShell:
                writer.WriteString("edit", "allow");
                WriteBashRules(writer, permission.Deny);
                break;
            case PermissionLevel.Full:
                writer.WriteString("edit", "allow");
                writer.WriteString("bash", "allow");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(permission));
        }

        if (permission.Level != PermissionLevel.Full)
            WriteHeadlessGuards(writer);

        writer.WriteEndObject();
    }

    // What `--auto` would otherwise hand away. Every 2.0.12 agent starts with
    // `{external_directory:ask}` and `{read: *.env|*.env.* → ask}`, and `--auto` approves whatever is
    // only *asked*, so below `full` those guards are no guard at all; `question` is worse than open,
    // since under ProcessRunner's closed stdin it stalls the run to its timeout. Denies survive
    // `--auto`. Order is load-bearing: opencode takes the **last** matching rule, so the
    // `.env.example` allow comes after the two denies — the order opencode writes its own in.
    private static void WriteHeadlessGuards(Utf8JsonWriter writer)
    {
        writer.WriteString("external_directory", "deny");
        writer.WriteString("question", "deny");
        writer.WriteStartObject("read");
        writer.WriteString("*.env", "deny");
        writer.WriteString("*.env.*", "deny");
        writer.WriteString("*.env.example", "allow");
        writer.WriteEndObject();
    }

    private static void WriteBashRules(Utf8JsonWriter writer, IReadOnlyList<string> deny)
    {
        writer.WriteStartObject("bash");
        writer.WriteString("*", "allow");

        // "<verb> *" matches the verb as a word ("git merge x", not "git merge-base"), and opencode's
        // matcher makes a trailing " *" optional so it also catches the bare verb — on the dev branch
        // read 2026-10-02 (util/wildcard.ts). The exact "<verb>" entry covers a build without that.
        foreach (string pattern in deny)
        {
            writer.WriteString(pattern, "deny");
            writer.WriteString($"{pattern} *", "deny");
        }

        writer.WriteEndObject();
    }
}
