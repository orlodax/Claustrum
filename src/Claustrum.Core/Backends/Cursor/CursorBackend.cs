using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Cursor;

// docs/PLAN.md §A3's "cursor" row, validated against a real `cursor-agent 2026.09.18-9a7762b` on
// 2026-09-21 (issues #13/#14): the argv below, the result JSON's field names and the permission
// mapping are measured now, not guessed — NOTES.md "The cursor backend, validated against a real
// install" logs every command and what it printed. Two measurements shape this class: `-p` refuses
// to start in an untrusted directory unless `--trust`/`-f`/`--yolo` is passed (exit 1, empty stdout,
// ~1s — it fails fast, it never stalls), and with no positional prompt it reads the whole prompt from
// stdin, which is how the rendered role body got off argv (#14) and why no length guard is left.
public sealed class CursorBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    // docs/PLAN.md §A3's "doctor marks it advisory", finally said out loud (issue #17). An advisory,
    // not a Problem: a problem would make `--probe` skip cursor's paid round trip.
    private const string DenyAdvisory =
        """deny list is enforced by prompt only: cursor-agent has no native deny flag (NOTES.md "The cursor backend, validated against a real install", box 11)""";

    public string Name => "cursor";

    // The advisory holds whether or not the binary is here: it describes cursor-agent itself, not
    // this install, so a machine without cursor is told the same thing before it installs one.
    public async Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken)
    {
        Doctor doctor = await VersionProbe.RunAsync("cursor-agent", ["--version"], config, platform, cancellationToken, detectTimeout);
        return doctor with { Advisories = [DenyAdvisory] };
    }

    public ProcessSpec Build(ResolvedRun run)
    {
        string systemPrompt = File.ReadAllText(run.SystemPromptFilePath);
        string prompt = BuildPrompt(systemPrompt, run.Brief, run.Role.Permission.Level, run.Role.Permission.Deny);

        List<string> args = ["-p", "--output-format", "json", "--model", run.Role.Model];
        args.AddRange(PermissionArgs(run.Role.Permission));
        args.AddRange(["--workspace", run.Cwd]);

        // Confirmed live with the session_id a previous run returned: the resumed chat kept its
        // history (122 input tokens for the follow-up turn) and answered about the file it had made.
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);

        return new ProcessSpec("cursor-agent", [.. args], run.Cwd, run.Env, [], prompt);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();

        // Measured, not defensive: cursor's startup refusals (workspace trust, and "Named models
        // unavailable ... Free plans can only use Auto") print a human line on stderr, leave stdout
        // completely empty and exit 1 — same shape as copilot's own auth failure.
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // `--output-format json` emitted exactly one JSON object, on one line, in every run
            // captured (never JSONL, never several documents), so this only catches a stdout that
            // was not JSON at all — a `text` output format, or a banner cursor might add later.
            return new ParsedOutput(trimmed, null, null, null, [], null, IsError: exitCode != 0);
        }

        using (document)
        {
            JsonElement root = document.RootElement.Clone();
            string finalMessage = root.TryGetProperty("result", out JsonElement resultProp) ? resultProp.GetString() ?? "" : trimmed;
            string? sessionId = root.TryGetProperty("session_id", out JsonElement sessionProp) ? sessionProp.GetString() : null;
            Usage? usage = root.TryGetProperty("usage", out JsonElement usageProp) ? ParseUsage(usageProp) : null;
            bool isError = exitCode != 0 || (root.TryGetProperty("is_error", out JsonElement errorProp) && errorProp.ValueKind == JsonValueKind.True);

            // No cost anywhere in the result object (`duration_ms`, `request_id` and `usage` are all
            // it carries beyond these), and cursor-agent has no budget flag either — so `--budget`
            // neither caps nor accounts for a cursor run, and CostUsd is null by measurement.
            return new ParsedOutput(finalMessage, sessionId, null, usage, [], root, isError);
        }
    }

    // No native deny mechanism (docs/PLAN.md §A3: "the deny list is appended to the system prompt as
    // a hard rule and doctor marks it advisory") — `cursor-agent --help` lists no per-command deny
    // flag at all, only the blanket `-f/--yolo`, so this stays enforcement by instruction. Plan mode
    // is the one real permission boundary cursor offers, and PermissionArgs uses it where it fits.
    private static string BuildPrompt(string systemPrompt, string brief, PermissionLevel level, string[] deny)
    {
        StringBuilder prompt = new(systemPrompt);

        string[] levelRules = LevelRules(level);
        if (levelRules.Length > 0 || deny.Length > 0)
            prompt.Append("\n\n## Hard rules (never violate)\n");

        foreach (string rule in levelRules)
            prompt.Append("- ").Append(rule).Append('\n');
        foreach (string pattern in deny)
            prompt.Append("- Never run: ").Append(pattern).Append('\n');

        prompt.Append("\n\n# Task\n").Append(brief);
        return prompt.ToString();
    }

    // What plan mode does not cover. Measured: in `--mode plan` a read-only command still runs (`git
    // log --oneline -1` came back with the real HEAD), so "read-only" needs the rule below; Edit has
    // no native shape at all in cursor, so its no-shell half is entirely carried by the prompt.
    private static string[] LevelRules(PermissionLevel level) => level switch
    {
        PermissionLevel.ReadOnly =>
        [
            "This run is READ-ONLY: never run a command that changes anything, and report a needed fix instead of applying it.",
        ],
        PermissionLevel.Edit => ["Never run a shell command: edit files and report, nothing else."],
        _ => [],
    };

    // Measured 2026-09-21, one paid run each. `--mode plan -p` returns headless with stdin closed
    // (18s, exit 0) and created neither the file nor the shell-redirect it was explicitly asked for
    // ("Plan mode blocks both of those actions"), while a read-only command in the same mode ran —
    // so plan mode is the native read-the-tree-change-nothing rung for ReadOnly and Shell, and `-f`
    // rides along at every level because it is also what satisfies the workspace-trust gate.
    private static List<string> PermissionArgs(PermissionPolicy permission) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ["--mode", "plan", "-f"],
        PermissionLevel.Shell => ["--mode", "plan", "-f"],
        PermissionLevel.Edit => ["-f"],
        PermissionLevel.EditShell => ["-f"],
        PermissionLevel.Full => ["-f", "--sandbox", "disabled"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    // camelCase, unlike claude's snake_case `usage` object, and `cacheWriteTokens` where claude says
    // `cache_creation_input_tokens` — read off a real capture (tests/fixtures/cursor/success.json).
    private static Usage ParseUsage(JsonElement usage) => new(
        TryGetInt(usage, "inputTokens"),
        TryGetInt(usage, "outputTokens"),
        TryGetInt(usage, "cacheReadTokens"),
        TryGetInt(usage, "cacheWriteTokens"));

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;
}
