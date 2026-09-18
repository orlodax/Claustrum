using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Copilot;

// docs/PLAN.md §A3 "copilot" row, checked against a real `@github/copilot` 1.0.86 install (2026-09-18
// — see NOTES.md "The copilot backend" for exactly what `copilot --help`/`copilot help environment`
// confirmed vs. what stayed a best-effort guess). Confirmed live: `-C`, `--agent`, `--output-format
// json` (JSONL), `--allow-tool`/`--deny-tool` with `shell(...)`/`write` tool names, `--mode`
// [interactive|plan|autopilot], `--reasoning-effort` (not `--effort`, and "high"/"xhigh"/"max" are
// valid values there), and that `--add-dir <dir>` "loads that directory's .github/skills and
// .github/agents as trusted configuration" — a real, cwd-relative mechanism, not the `COPILOT_HOME`
// relocation the original plan guessed. No authenticated session was reachable from this environment
// (no GitHub Copilot subscription token here — the sandbox's own repo-scoped GITHUB_TOKEN is a
// different, narrower credential and was deliberately not pressed into this instead), so the exact
// `.agent.md` frontmatter schema and every JSONL success-event field name are unconfirmed inference,
// flagged the same way as the other M3 backends' fixtures.
public sealed class CopilotBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    public string Name => "copilot";

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(Name, ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        string agentName = $"claustrum-{run.Role.Name}";

        // A scratch directory under the job, not run.Cwd: --add-dir loads *any* directory's own
        // .github/agents, so the target repo never needs a Claustrum-owned file committed into it.
        string agentRoot = Path.Combine(run.JobDirectory, "copilot-agents");
        string agentsDirectory = Path.Combine(agentRoot, ".github", "agents");
        Directory.CreateDirectory(agentsDirectory);
        string agentFilePath = Path.Combine(agentsDirectory, $"{agentName}.agent.md");
        File.WriteAllText(agentFilePath, BuildAgentFile(agentName, File.ReadAllText(run.SystemPromptFilePath)));

        List<string> args =
        [
            "-C", run.Cwd,
            "--agent", agentName,
            "--add-dir", agentRoot,
            "--output-format", "json",
            "--log-level", "none",
            "--stream", "off",
        ];
        args.AddRange(PermissionArgs(run.Role.Permission));
        if (!string.IsNullOrEmpty(run.Role.Effort))
            args.AddRange(["--reasoning-effort", run.Role.Effort]);
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);
        args.AddRange(["-p", run.Brief]);

        return new ProcessSpec(Name, [.. args], run.Cwd, run.Env, [agentFilePath]);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();

        // Confirmed live (2026-09-18): an unauthenticated/fatal-startup failure prints a
        // human-readable message to stderr and leaves stdout empty, exit code 1 — not a JSON error
        // object the way opencode's own startup failures are. This branch is a real, not fabricated,
        // observation; everything past it (the JSONL success shape) is unconfirmed inference.
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        string? sessionId = null;
        string? lastText = null;
        Usage? usage = null;
        JsonElement? lastEvent = null;

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
                lastEvent = root.Clone();
                sessionId = FirstString(root, "session_id", "sessionID") ?? sessionId;
                lastText = ExtractText(root) ?? lastText;
                usage = ExtractUsage(root) ?? usage;
            }
        }

        // Every JSONL line failed to parse (or none carried recognizable text): fall back to the raw
        // stream rather than reporting an empty message, same fallback ClaudeBackend/OpencodeBackend
        // use for their own malformed/unrecognized-shape inputs.
        string finalMessage = lastText ?? trimmed;
        return new ParsedOutput(finalMessage, sessionId, null, usage, [], lastEvent, IsError: exitCode != 0);
    }

    // ReadOnly/Edit intentionally diverge from a literal reading of docs/PLAN.md §A3's permission
    // table: `--allow-all-tools` is documented by `copilot --help` itself as "required for
    // non-interactive mode", so a mapping that omits it (as the original table's ReadOnly/Edit rows
    // did) risks `-p` hanging on an interactive confirmation prompt that can never be answered
    // headlessly. Every level below instead grants broadly via --allow-all-tools and narrows with
    // --deny-tool, which the CLI's own examples show composing (deny wins) — the same
    // allow-broad-deny-narrow shape ClaudeBackend already uses for EditShell.
    private static List<string> PermissionArgs(PermissionPolicy permission) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ["--allow-all-tools", "--mode", "plan", "--deny-tool", "write", "--deny-tool", "shell"],
        PermissionLevel.Edit => ["--allow-all-tools", "--allow-all-paths", "--deny-tool", "shell"],
        PermissionLevel.EditShell => EditShellArgs(permission.Deny),
        PermissionLevel.Full => ["--allow-all"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    private static List<string> EditShellArgs(string[] deny)
    {
        List<string> args = ["--allow-all-tools", "--allow-all-paths"];
        foreach (string pattern in deny)
            args.AddRange(["--deny-tool", $"shell({pattern})"]);
        return args;
    }

    // Frontmatter shape is an unconfirmed guess (no authenticated session to check it against),
    // modeled on the same name/description convention Claude Code's own .claude/agents/*.md uses,
    // since GitHub Copilot CLI's .github/agents/*.agent.md is documented as an analogous mechanism.
    private static string BuildAgentFile(string agentName, string systemPrompt) =>
        $"""
        ---
        name: {agentName}
        description: Claustrum-rendered role, do not edit by hand.
        ---
        {systemPrompt}
        """;

    private static string? FirstString(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
            if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    // No confirmed example of a successful run's JSONL shape exists (see the class comment), so this
    // tries every plausible key an assistant-text event might use rather than committing to one.
    private static string? ExtractText(JsonElement root)
    {
        if (FirstString(root, "content", "text") is { Length: > 0 } direct)
            return direct;

        if (root.TryGetProperty("message", out JsonElement message))
        {
            if (message.ValueKind == JsonValueKind.String)
                return message.GetString();
            if (FirstString(message, "content", "text") is { Length: > 0 } nested)
                return nested;
        }

        return null;
    }

    private static Usage? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usageElement))
            return null;

        int? input = TryGetInt(usageElement, "input_tokens") ?? TryGetInt(usageElement, "prompt_tokens");
        int? output = TryGetInt(usageElement, "output_tokens") ?? TryGetInt(usageElement, "completion_tokens");
        return input is null && output is null ? null : new Usage(input, output, null, null);
    }

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;
}
