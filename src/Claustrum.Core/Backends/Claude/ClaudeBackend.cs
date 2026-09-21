using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Claude;

// argv and permission->flag mapping follow docs/PLAN.md A3. `--append-system-prompt-file` DOES
// exist on `claude` 2.1.269 — `.hideHelp()` keeps it off `claude --help`, and claude silently
// ignores unrecognized flags, so the M1 pass mistook silent-ignore for "flag missing" and switched
// to inline `--append-system-prompt` (wrong; see NOTES.md "Role injection per backend"). Parse
// auto-detects single-object `json` output vs `stream-json` JSONL by trying to parse stdout as one
// JSON document first (IBackend.Parse has no `--stream` flag to consult); this holds because a
// JSONL stream always has trailing content after the first line closes, which JsonDocument rejects.
public sealed class ClaudeBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    // --permission-prompts none is passed for every level, including Full, so a headless run never
    // blocks on a prompt regardless of mode. ReadOnly's --allowedTools additionally lets a blind
    // code-reviewer read the diff it was asked to review (git/gh are read-only queries, not edits).
    private const string ReadOnlyTools = "Read,Glob,Grep,Bash(git diff*),Bash(git log*),Bash(git show*),Bash(git status*),Bash(gh pr *),Bash(gh issue *)";

    public string Name => "claude";

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(Name, ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        bool resuming = run.ResumeSession is { Length: > 0 };

        List<string> args = ["-p"];
        args.AddRange(run.Stream ? ["--output-format", "stream-json", "--verbose"] : ["--output-format", "json"]);
        args.AddRange(["--model", run.Role.Model]);
        args.AddRange(["--append-system-prompt-file", run.SystemPromptFilePath]);
        args.AddRange(PermissionArgs(run.Role.Permission));

        // A resumed session must keep its own persisted history; --no-session-persistence would
        // wipe exactly the state --resume is asking to reuse.
        if (!resuming)
            args.Add("--no-session-persistence");

        if (run.BudgetUsd is { } budget)
            args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrEmpty(run.Role.Effort))
            args.AddRange(["--effort", run.Role.Effort]);

        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);

        args.Add(run.Brief);

        return new ProcessSpec(Name, [.. args], run.Cwd, run.Env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            return BuildFromResult(document.RootElement.Clone(), exitCode, []);
        }
        catch (JsonException)
        {
            return ParseStream(trimmed, exitCode);
        }
    }

    private static List<string> PermissionArgs(PermissionPolicy permission) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ["--permission-mode", "plan", "--permission-prompts", "none", "--allowedTools", ReadOnlyTools],
        PermissionLevel.Shell => ShellArgs(permission.Deny),
        PermissionLevel.Edit => ["--permission-mode", "acceptEdits", "--permission-prompts", "none", "--disallowedTools", "Bash"],
        PermissionLevel.EditShell => EditShellArgs(permission.Deny),
        PermissionLevel.Full => ["--dangerously-skip-permissions", "--permission-prompts", "none"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    // Shell keeps ReadOnly's plan mode (nothing may be written) but opens Bash and leaves MCP tools
    // alone: --allowedTools names only the built-ins, so a Browser MCP server stays reachable, which
    // is the whole point of the level (ui-reviewer's environment part requires one).
    private static List<string> ShellArgs(string[] deny)
    {
        List<string> args = ["--permission-mode", "plan", "--permission-prompts", "none", "--disallowedTools", "Edit,Write,NotebookEdit"];
        if (deny.Length > 0)
            args.AddRange(["--disallowedTools", string.Join(',', deny.Select(pattern => $"Bash({pattern}*)"))]);
        return args;
    }

    private static List<string> EditShellArgs(string[] deny)
    {
        List<string> args = ["--permission-mode", "acceptEdits", "--permission-prompts", "none", "--allowedTools", "Edit,Write,Read,Glob,Grep,Bash(*)"];
        if (deny.Length > 0)
            args.AddRange(["--disallowedTools", string.Join(',', deny.Select(pattern => $"Bash({pattern}*)"))]);
        return args;
    }

    private static ParsedOutput ParseStream(string text, int exitCode)
    {
        JsonElement? lastResult = null;
        List<ChangedFile> reportedEdits = [];

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
                string? type = root.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;
                if (type == "result")
                    lastResult = root.Clone();
                else if (type == "assistant")
                    CollectToolUseEdits(root, reportedEdits);
            }
        }

        return lastResult is { } result
            ? BuildFromResult(result, exitCode, reportedEdits)
            : new ParsedOutput(text, null, null, null, [.. reportedEdits], null, IsError: exitCode != 0);
    }

    private static ParsedOutput BuildFromResult(JsonElement root, int exitCode, List<ChangedFile> reportedEdits)
    {
        string finalMessage = FinalMessage(root);
        string? sessionId = root.TryGetProperty("session_id", out JsonElement sessionProp) ? sessionProp.GetString() : null;
        decimal? cost = root.TryGetProperty("total_cost_usd", out JsonElement costProp) && costProp.TryGetDecimal(out decimal costValue) ? costValue : null;
        bool isError = root.TryGetProperty("is_error", out JsonElement errorProp) && errorProp.ValueKind == JsonValueKind.True;
        Usage? usage = root.TryGetProperty("usage", out JsonElement usageProp) ? ParseUsage(usageProp) : null;

        return new ParsedOutput(finalMessage, sessionId, cost, usage, [.. reportedEdits], root, isError || exitCode != 0);
    }

    // 2026-09-21 (issue #12): a result document that claude terminated on its own budget flag carries
    // no `result` at all — the text sits in an `errors` array — so reading `result` alone left
    // FinalMessage and Error empty and `doctor --probe` printed "failed (no error message)".
    private static string FinalMessage(JsonElement root)
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

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;

    private static string? TryGetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void CollectToolUseEdits(JsonElement root, List<ChangedFile> edits)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement typeProp) || typeProp.GetString() != "tool_use")
                continue;

            string? name = item.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() : null;
            if (name is not ("Edit" or "Write"))
                continue;

            string? path = item.TryGetProperty("input", out JsonElement input) && input.TryGetProperty("file_path", out JsonElement pathProp)
                ? pathProp.GetString()
                : null;

            if (path is not null)
                edits.Add(new ChangedFile(path, name == "Write" ? ChangeKind.Added : ChangeKind.Modified));
        }
    }
}
