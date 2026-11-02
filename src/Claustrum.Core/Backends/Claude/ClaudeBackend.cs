using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Core.Report;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends.Claude;

// argv and permission->flag mapping follow docs/PLAN.md A3. `--append-system-prompt-file` DOES
// exist on `claude` 2.1.269 — `.hideHelp()` keeps it off `claude --help`, and claude silently
// ignores unrecognized flags, so the M1 pass mistook silent-ignore for "flag missing" and switched
// to inline `--append-system-prompt` (wrong; see NOTES.md "Role injection per backend"). The brief
// goes in on stdin, never argv (#68, 2026-10-08, the cursor shape): `claude -p` reads its prompt
// from stdin when argv carries none. Parse tells single-object `json` from `stream-json` JSONL by
// parsing stdout as one JSON document first (a JSONL stream has trailing content, which JsonDocument
// rejects). Every run is stream-json since 2026-10-15; the single-document form still parses.
public sealed class ClaudeBackend(IPlatform platform) : IBackend, IPostRunHook
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    public IStreamFormatter? CreateStreamFormatter(string cwd) => new ClaudeStreamFormatter(cwd);

    // --permission-prompts none is passed for every level, including Full, so a headless run never
    // blocks on a prompt regardless of mode. ReadOnly's --allowedTools additionally lets a blind
    // code-reviewer read the diff it was asked to review (ReadOnlyShell: read-only git and gh verbs).
    // Not measured: whether claude's matcher can be fooled by quote-splitting or `git diff --no-index`; residual under #148.
    private static readonly string readOnlyTools = string.Join(',', ["Read", "Glob", "Grep", .. ReadOnlyShell.Commands.Select(command => $"Bash({command})")]);

    // Claude Code's native subagent tool: `Agent` on 2.1.278, `Task` on older builds — an unknown
    // name is silently ignored, so naming both costs nothing (NOTES.md "The native subagent tool is
    // called Agent, and only --disallowedTools takes it away").
    private const string SubagentTools = "Agent,Task";

    public string Name => "claude";

    public ILiveProtocol? LiveProtocol => ClaudeLiveProtocol.Instance;

    public bool HonoursReadDirs => true;

    public string? ReadDirRefusal(string dir, PermissionLevel level) => ReadDirRefusalFor(dir, level);

    public RunTrace? Trace(string stdout) => ClaudeTrace.Read(stdout);

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(Name, ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        bool resuming = run.ResumeSession is { Length: > 0 };

        List<string> args = ["-p"];

        // Always stream-json: a single `json` document lands only at exit, which left `jobs follow` (and an
        // MCP delegate) with nothing to show for the whole run. `--stream` now only gates the stderr echo.
        args.AddRange(["--output-format", "stream-json", "--verbose"]);

        // A live session (#124) feeds the brief and every later message as stream-json user turns on a stdin that stays open; without
        // the flag claude would read stdin once, as the prompt, and stop. --replay-user-messages echoes each message when claude takes it (#127).
        if (run.Live)
            args.AddRange(["--input-format", "stream-json", "--replay-user-messages"]);

        args.AddRange(["--model", run.Role.Model]);
        args.AddRange(["--append-system-prompt-file", run.SystemPromptFilePath]);

        // #128: without --strict-mcp-config every role loaded the operator's user-level servers (~12k cache-creation
        // tokens a turn, and personal connectors on a headless run). `user_mcp: true` is the opt-back-in.
        McpSelection mcp = run.Role.UserMcp ? new McpSelection(null, [ClaudeMcpConfig.UserServers]) : ClaudeMcpConfig.Select(platform, run.Cwd, run.JobDirectory);
        if (!run.Role.UserMcp)
            args.Add("--strict-mcp-config");
        if (mcp.ConfigPath is not null)
            args.AddRange(["--mcp-config", mcp.ConfigPath]);

        (string[] approved, string? approvalWarning) = run.Role.Permission.Level == PermissionLevel.Shell ? ApprovedMcpServers(run, mcp) : ([], null);
        args.AddRange(PermissionArgs(run.Role.Permission, approved));
        args.AddRange(ReadDirArgs(run.ReadDirs, run.Role.Permission.Level));

        // Spawned by `coordinate` (§D3): a native subagent would run outside the cast and outside
        // the tree ledger, and no permission level withholds it — measured 2026-09-22, `Agent` is in
        // the request's tool list even under `--permission-mode plan --allowedTools`. Repeated
        // --disallowedTools merge, so this adds to the level's own entry instead of replacing it.
        if (run.Env.ContainsKey(BudgetLedger.TreeVariable))
            args.AddRange(["--disallowedTools", SubagentTools]);

        // needs_input is continued with --resume, which a session that was never saved cannot do (#127): only a role that cannot end that way drops it.
        if (!resuming && (run.Role.Blind || !run.Role.HasReport))
            args.Add("--no-session-persistence");

        if (run.BudgetUsd is { } budget)
            args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrEmpty(run.Role.Effort))
            args.AddRange(["--effort", run.Role.Effort]);

        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);

        // On argv the brief showed in every process listing: a `pgrep -f` pattern a brief quoted matched
        // the job running it (#68, measured 2026-10-08). Stdin also lifts the argv length cap.
        string stdinText = run.Live ? ClaudeLiveProtocol.Instance.EncodeUserMessage(run.Brief, Guid.NewGuid().ToString()) + "\n" : run.Brief;
        List<string> warnings = [];
        if (mcp.Warning is { } mcpWarning)
            warnings.Add(mcpWarning);
        if (approvalWarning is not null)
            warnings.Add(approvalWarning);

        return new ProcessSpec(Name, [.. args], run.Cwd, run.Env, [], StdinText: stdinText, McpServers: mcp.Servers, Warnings: warnings.Count > 0 ? [.. warnings] : null);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;

            // A stream cut down to one line (killed after `init`) is a valid document too, but not a result.
            return root.ValueKind == JsonValueKind.Object && TryGetString(root, "type") is { } type && type != "result"
                ? ParseStream(trimmed, stderr, exitCode)
                : BuildFromResult(root.Clone(), exitCode, []);
        }
        catch (JsonException)
        {
            return ParseStream(trimmed, stderr, exitCode);
        }
    }

    // IPostRunHook (#127; NOTES.md "A claude continuation is charged what it added"): `--resume` restores the session's running total into
    // `total_cost_usd`, so a continuation is charged the total minus the last receipt's. Usage is the run's own on `--resume` (measured), and is left alone.
    Task<PreRunBaseline?> IPostRunHook.PreRunAsync(PostRunContext context, CancellationToken cancellationToken)
    {
        if (context.ResumeSession is not { Length: > 0 } sessionId)
            return Task.FromResult<PreRunBaseline?>(null);

        decimal? before = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(context.JobDirectory)) is { Length: > 0 } jobsRoot
            ? ClaudeSessionCost.Before(jobsRoot, context.JobDirectory, sessionId)
            : null;

        return Task.FromResult<PreRunBaseline?>(new PreRunBaseline(before, null, before is null ? "no receipt in the job store names this session" : null));
    }

    Task<PostRunResult?> IPostRunHook.PostRunAsync(ParsedOutput parsed, PostRunContext context, PreRunBaseline? baseline, CancellationToken cancellationToken)
    {
        // A baseline only applies when the run really continued that session: `--resume` keeps the id.
        if (baseline is null || parsed.CostUsd is not { } total || parsed.SessionId != context.ResumeSession)
            return Task.FromResult<PostRunResult?>(null);

        PostRunResult result = baseline.CostUsd switch
        {
            null => new PostRunResult(null, null, $"what claude session {context.ResumeSession} had cost before this run is unknown ({baseline.Failure}): charged its whole running total"),
            { } before when total < before => new PostRunResult(null, null, "the resumed session's total is lower than its last receipt's: charged the whole running total"),
            { } before => new PostRunResult(total - before, null, null),
        };

        return Task.FromResult<PostRunResult?>(result);
    }

    private static List<string> PermissionArgs(PermissionPolicy permission, string[] mcpServers) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ReadOnlyArgs(permission.Deny),
        PermissionLevel.Shell => ShellArgs(permission.Deny, mcpServers),
        // No deny patterns: they are all `Bash(...)`, and this level withholds Bash outright.
        PermissionLevel.Edit => ["--permission-mode", "acceptEdits", "--permission-prompts", "none", "--disallowedTools", "Bash"],
        PermissionLevel.EditShell => EditShellArgs(permission.Deny),
        PermissionLevel.Full => ["--dangerously-skip-permissions", "--permission-prompts", "none"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    // Deny applies here too (#54): the role's own list holds on top of readOnlyTools.
    private static List<string> ReadOnlyArgs(string[] deny)
    {
        List<string> args = ["--permission-mode", "plan", "--permission-prompts", "none", "--allowedTools", readOnlyTools];
        if (deny.Length > 0)
            args.AddRange(["--disallowedTools", BashDenyRules(deny)]);
        return args;
    }

    // #140: not plan mode, which also tells the model to change nothing, so a ui-reviewer refused every walk that writes app data
    // (job 20261026-121557-5a29c69e). dontAsk runs what the allow-list names and denies the rest without a prompt: the edit tools are
    // gone, Bash runs, and an MCP server's tools only when it is named, since `mcp__*` approves nothing (measured 2026-11-02, 2.1.316).
    private static List<string> ShellArgs(string[] deny, string[] mcpServers)
    {
        string allowed = string.Join(',', ["Read", "Glob", "Grep", "Bash(*)", .. mcpServers.Select(server => $"mcp__{server}__*")]);
        List<string> args = ["--permission-mode", "dontAsk", "--permission-prompts", "none", "--allowedTools", allowed, "--disallowedTools", "Edit,Write,NotebookEdit"];
        if (deny.Length > 0)
            args.AddRange(["--disallowedTools", BashDenyRules(deny)]);
        return args;
    }

    // What a shell-level run may call unasked: the role's own servers (library `tools`, user config `mcp_servers`), never a repo's
    // `.mcp.json` ones, which a cloned repo controls (#127's rule). Approval is by name, so a name the repo's file defines is dropped
    // even when the role names it: the repo could define its own server as `chrome`. Spelled as claude's tool names.
    private (string[] Approved, string? Warning) ApprovedMcpServers(ResolvedRun run, McpSelection mcp)
    {
        string[] wanted = ToolServerNames(run.Role.McpServers ?? []);
        if (wanted.Length == 0)
            return ([], null);

        string[]? defined = run.Role.UserMcp ? ClaudeMcpConfig.DeclaredNames(platform, run.Cwd) : mcp.Servers;
        if (defined is null)
            return ([], $".mcp.json in {run.Cwd} could not be read for its server names, so no MCP server is approved unasked ({string.Join(", ", wanted)} denied): a repo may not grant a role tools");

        HashSet<string> repo = [.. ToolServerNames(defined)];
        string[] dropped = [.. wanted.Where(repo.Contains)];
        return dropped.Length == 0
            ? (wanted, null)
            : ([.. wanted.Except(dropped)], $".mcp.json in {run.Cwd} defines {string.Join(", ", dropped)}: not approved unasked, though the role names it, because a repo may not grant a role tools; its tools are denied");
    }

    private static string[] ToolServerNames(IEnumerable<string> servers) =>
        [.. servers
            .Where(server => !string.IsNullOrWhiteSpace(server))
            .Select(server => string.Concat(server.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')))
            .Distinct(StringComparer.Ordinal)];

    // #144: below full (whose bypass reaches every path already) each read dir becomes a working directory. At the edit levels an
    // `Edit(//<dir>/**)` deny keeps it read-only: one rule stopped Write, Edit, NotebookEdit and a Bash redirect there (measured 2026-11-02).
    private static List<string> ReadDirArgs(string[]? readDirs, PermissionLevel level)
    {
        List<string> args = [];
        if (readDirs is not { Length: > 0 } || level == PermissionLevel.Full)
            return args;

        string[] given = [.. readDirs.Where(dir => ReadDirRefusalFor(dir, level) is null)];
        foreach (string dir in given)
            args.AddRange(["--add-dir", dir]);
        if (level is PermissionLevel.Edit or PermissionLevel.EditShell)
        {
            foreach (string dir in given)
                args.AddRange(["--disallowedTools", EditDenyRule(dir)]);
        }

        return args;
    }

    // claude 2.1.316 splits --disallowedTools on `,` and spaces outside parentheses it tracks with a flag, not a depth, and no escape
    // helps: for `/x/Odoo (copy) 2` the Edit deny named no path (replayed 2026-11-02), and an added dir is auto-accepted at acceptEdits.
    private static string? ReadDirRefusalFor(string dir, PermissionLevel level) =>
        level is PermissionLevel.Edit or PermissionLevel.EditShell && dir.IndexOfAny(['(', ')', ',']) >= 0
            ? $"claude cannot keep {dir} read-only (its path has ( ) or ,)"
            : null;

    // `//` anchors at the filesystem root, and a Windows path is matched in POSIX form, `C:\x` as `/c/x`. Gitignore's pattern
    // characters are escaped, so the rule names the directory as spelled (code.claude.com/docs/en/permissions, read 2026-11-02).
    private static string EditDenyRule(string dir)
    {
        string path = OperatingSystem.IsWindows() ? dir.Replace('\\', '/') : dir;
        if (OperatingSystem.IsWindows() && path is [char drive, ':', ..] && char.IsAsciiLetter(drive))
            path = $"/{char.ToLowerInvariant(drive)}{path[2..]}";

        string escaped = string.Concat(path.Select(c => c is '*' or '?' or '[' or ']' or '\\' ? $"\\{c}" : c.ToString()));
        return $"Edit(/{escaped}/**)";
    }

    private static List<string> EditShellArgs(string[] deny)
    {
        List<string> args = ["--permission-mode", "acceptEdits", "--permission-prompts", "none", "--allowedTools", "Edit,Write,Read,Glob,Grep,Bash(*)"];
        if (deny.Length > 0)
            args.AddRange(["--disallowedTools", BashDenyRules(deny)]);
        return args;
    }

    // `Bash(git merge *)`, with the space: the verb as a word, so `git merge-base` stays open (the old
    // `Bash(git merge*)` caught it). The exact `Bash(git merge)` beside it keeps the bare verb denied
    // even when the entry carries its own `*` ("git push*"): a trailing ` *` matches the bare command
    // only as the rule's sole wildcard (code.claude.com/docs/en/permissions, read 2026-10-02).
    private static string BashDenyRules(string[] deny)
    {
        IEnumerable<string> rules = deny.SelectMany(pattern => (string[])[$"Bash({pattern})", $"Bash({pattern} *)"]);
        return string.Join(',', rules);
    }

    private static ParsedOutput ParseStream(string text, string stderr, int exitCode)
    {
        JsonElement? lastResult = null;
        string? reportMessage = null;
        List<ChangedFile> reportedEdits = [];
        bool sawEvent = false;

        // #126: a tool call is work unless it came back as an errored result (denied, not found: it did nothing), so one still open when
        // the stream stops counts. Several `result` events are a live session's turns: a human spoke, and the count is not trusted.
        HashSet<string> toolCalls = [];
        int results = 0;
        string? assistantError = null;

        foreach (string rawLine in text.Split('\n'))
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

                sawEvent = true;

                // A replayed user message (#127) is an echo, not an event of the run: --resume can replay the session's whole history.
                if (ClaudeLiveProtocol.IsReplay(root))
                    continue;

                string? type = TryGetString(root, "type");
                if (type == "result")
                {
                    results++;
                    lastResult = root.Clone();
                    string message = FinalMessage(root);
                    if (ReportExtractor.Extract(message).Status != ReportStatus.Missing)
                        reportMessage = message;
                }
                else if (type == "assistant")
                {
                    CollectToolUseEdits(root, reportedEdits);
                    CollectToolCalls(root, toolCalls);
                    assistantError = TryGetString(root, "error") ?? assistantError;
                }
                else if (type == "user")
                    ForgetErroredToolCalls(root, toolCalls);
            }
        }

        // No result line (a killed run): events are noise as a message, so stderr speaks if it has anything.
        if (lastResult is not { } result)
            return new ParsedOutput(sawEvent && stderr.Trim().Length > 0 ? stderr : text, null, null, null, [.. reportedEdits], null, IsError: exitCode != 0);

        // A live session (#124) has one `result` per turn and the cost is the last one's running total. The text
        // is the last turn's too, unless a message that crossed the report on stdin made a turn after it: then
        // the turn that carries the report stays the final message.
        ParsedOutput parsed = BuildFromResult(result, exitCode, reportedEdits);
        parsed = parsed with { ToolCallsCompleted = results == 1 ? toolCalls.Count : null, Steps = results == 1 ? NumTurns(result) : null };
        if (parsed.ErrorKind == ErrorKind.Other && assistantError is not null)
            parsed = parsed with { ErrorKind = ClassifyAssistantError(assistantError) };

        return reportMessage is not null && ReportExtractor.Extract(parsed.FinalMessage).Status == ReportStatus.Missing
            ? parsed with { FinalMessage = reportMessage }
            : parsed;
    }

    private static ParsedOutput BuildFromResult(JsonElement root, int exitCode, List<ChangedFile> reportedEdits)
    {
        string finalMessage = FinalMessage(root);
        string? sessionId = root.TryGetProperty("session_id", out JsonElement sessionProp) ? sessionProp.GetString() : null;
        decimal? cost = root.TryGetProperty("total_cost_usd", out JsonElement costProp) && costProp.TryGetDecimal(out decimal costValue) ? costValue : null;
        bool isError = root.TryGetProperty("is_error", out JsonElement errorProp) && errorProp.ValueKind == JsonValueKind.True;
        Usage? usage = root.TryGetProperty("usage", out JsonElement usageProp) ? ParseUsage(usageProp) : null;

        // #126: an `is_error` result with an `api_error_status` is the provider refusing (recorded 2026-09-13, tests/fixtures/claude/error.txt:
        // 404, an unknown model, for $0). A failure with no such status is Other, and a `terminal_reason` of api_error without one is the
        // connection (no HTTP answer to carry a status). Its text is the provider's own words. ToolCallsCompleted is the stream's to fill.
        ErrorKind? kind = null;
        string? detail = null;
        if (isError)
        {
            string? terminal = TryGetString(root, "terminal_reason");
            kind = root.TryGetProperty("api_error_status", out JsonElement statusProp) && statusProp.ValueKind == JsonValueKind.Number && statusProp.TryGetInt32(out int status)
                ? ProviderErrors.Classify(status, null)
                : terminal == "api_error" ? ErrorKind.ProviderTransport : ErrorKind.Other;
            detail = finalMessage.Length > 0 ? finalMessage : null;
        }

        return new ParsedOutput(finalMessage, sessionId, cost, usage, [.. reportedEdits], root, isError || exitCode != 0, kind, detail);
    }

    // The SDK's documented `error` on an assistant message, read only to place a failure the result left as Other (no HTTP status on it).
    private static ErrorKind ClassifyAssistantError(string error) => error switch
    {
        "authentication_failed" => ErrorKind.ProviderAuth,
        "billing_error" or "rate_limit" => ErrorKind.ProviderQuota,
        "server_error" => ErrorKind.ProviderTransport,
        _ => ErrorKind.Other,
    };

    private static void CollectToolCalls(JsonElement root, HashSet<string> toolCalls)
    {
        foreach (JsonElement item in ContentItems(root))
        {
            if (TryGetString(item, "type") == "tool_use" && TryGetString(item, "id") is { } id)
                toolCalls.Add(id);
        }
    }

    private static void ForgetErroredToolCalls(JsonElement root, HashSet<string> toolCalls)
    {
        foreach (JsonElement item in ContentItems(root))
        {
            if (TryGetString(item, "type") == "tool_result"
                && TryGetString(item, "tool_use_id") is { } id
                && item.TryGetProperty("is_error", out JsonElement isError)
                && isError.ValueKind == JsonValueKind.True)
                toolCalls.Remove(id);
        }
    }

    private static IEnumerable<JsonElement> ContentItems(JsonElement root) =>
        root.TryGetProperty("message", out JsonElement message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out JsonElement content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    // 2026-09-21 (issue #12): a result document that claude terminated on its own budget flag carries
    // no `result` at all — the text sits in an `errors` array — so reading `result` alone left
    // FinalMessage and Error empty and `doctor --probe` printed "failed (no error message)".
    internal static string FinalMessage(JsonElement root)
    {
        if (TryGetString(root, "result") is { Length: > 0 } result)
            return result;

        if (root.TryGetProperty("errors", out JsonElement errorsProp) && errorsProp.ValueKind == JsonValueKind.Array)
        {
            string joined = string.Join("; ", errorsProp.EnumerateArray()
                .Where(error => error.ValueKind == JsonValueKind.String)
                .Select(error => error.GetString())
                .Where(text => text is { Length: > 0 }));

            if (joined.Length > 0)
                return joined;
        }

        // Last resort: the machine-readable reason, which at least names the failure class.
        return TryGetString(root, "subtype") is { } subtype && subtype.StartsWith("error_", StringComparison.Ordinal) ? subtype : "";
    }

    private static Usage ParseUsage(JsonElement usage) => new(
        TryGetInt(usage, "input_tokens"),
        TryGetInt(usage, "output_tokens"),
        TryGetInt(usage, "cache_read_input_tokens"),
        TryGetInt(usage, "cache_creation_input_tokens"));

    // #126: the result's own count of model requests (recorded: 1 for the 404 of tests/fixtures/claude/error.txt).
    private static int? NumTurns(JsonElement result) =>
        result.TryGetProperty("num_turns", out JsonElement turns) && turns.ValueKind == JsonValueKind.Number && turns.TryGetInt32(out int count) ? count : null;

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;

    internal static string? TryGetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void CollectToolUseEdits(JsonElement root, List<ChangedFile> edits)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || TryGetString(item, "type") != "tool_use")
                continue;

            string? name = TryGetString(item, "name");
            if (name is not ("Edit" or "Write"))
                continue;

            string? path = item.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object
                ? TryGetString(input, "file_path")
                : null;

            if (path is not null)
                edits.Add(new ChangedFile(path, name == "Write" ? ChangeKind.Added : ChangeKind.Modified));
        }
    }
}
