using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Copilot;

// docs/PLAN.md §A3 "copilot" row, validated end to end against a real, authenticated GitHub Copilot
// CLI 1.0.87 (2026-09-22, issue #13 — NOTES.md "The copilot backend, validated against a real
// install" holds the box-by-box evidence). Measured there, not guessed here: `--add-dir <dir>` does
// load that directory's `.github/agents`; the `.agent.md` frontmatter accepts `name`, `description`,
// `model`, `tools`, `infer` and `skills`, of which only `description` is required; and the JSONL is a
// stream of session events (`assistant.message` → `data.content`, `session.start` → `data.sessionId`)
// closed by the CLI's own `{"type":"result","sessionId":…,"exitCode":…}` line.
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
            "--model", run.Role.Model,
            "--output-format", "json",
            "--log-level", "none",
            "--stream", "off",
        ];
        args.AddRange(PermissionArgs(run.Role.Permission));

        // `--model auto` and `--reasoning-effort` are mutually exclusive: copilot 1.0.87 exits 1
        // before its first API call with `Model "auto" does not support reasoning effort
        // configuration` (measured 2026-09-22). `auto` is the one model id every account can use, so
        // the effort is dropped rather than the run; any other model that refuses effort still fails
        // loudly, with that same unambiguous message.
        if (!string.IsNullOrEmpty(run.Role.Effort) && run.Role.Model is not "auto")
            args.AddRange(["--reasoning-effort", run.Role.Effort]);
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);
        args.AddRange(["-p", run.Brief]);

        return new ProcessSpec(Name, [.. args], run.Cwd, run.Env, [agentFilePath]);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        string? sessionId = null;
        string? lastText = null;
        string? lastError = null;
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
                switch (StringProperty(root, "type"))
                {
                    case "session.start":
                        sessionId = DataString(root, "sessionId") ?? sessionId;
                        break;

                    // Last non-empty `assistant.message`, with no sub-agent filter: no delegation
                    // marker has ever been observed in a copilot stream (NOTES.md "The copilot
                    // backend, validated against a real install", sub-agent tagging).
                    case "assistant.message":
                        lastText = DataString(root, "content") ?? lastText;
                        break;

                    case "session.error":
                        lastError = DataString(root, "message") ?? lastError;
                        break;

                    case "result":
                        sessionId = StringProperty(root, "sessionId") ?? sessionId;
                        break;

                    default:
                        break;
                }
            }
        }

        // Nothing the model said: a startup failure. It goes to stderr, and stdout is *not*
        // necessarily empty — a `--model auto` + `--reasoning-effort` refusal still emitted two
        // `session.mcp_server_status_changed` lines first (measured 2026-09-22), which is why stderr
        // is preferred here over a stdout that only ever carried plumbing. The raw stream stays the
        // last resort, as it is for ClaudeBackend/OpencodeBackend's own unrecognized shapes.
        string? spoken = exitCode == 0 ? lastText ?? lastError : lastError ?? lastText;
        string finalMessage = spoken ?? (stderr.Trim().Length > 0 ? stderr : trimmed);

        // Usage is null by measurement, not by omission: the CLI suppresses `assistant.usage` from
        // the JSONL stream and its closing `result` event reports only `premiumRequests` and
        // durations — no token counts and no dollar cost anywhere (NOTES.md "The copilot backend,
        // validated against a real install"; `--usage-output-file` is the route if it is ever wanted).
        return new ParsedOutput(finalMessage, sessionId, null, null, [], lastEvent, IsError: exitCode != 0);
    }

    // Deviates from a literal reading of docs/PLAN.md §A3's permission table twice over; NOTES.md
    // "The copilot backend" and "The copilot backend, validated against a real install" (box 3) hold
    // the reasoning and the measurements. Every rung grants broadly with `--allow-all-tools`, which
    // `copilot --help` calls "required for non-interactive mode", and narrows with `--deny-tool`
    // (deny always wins) — ClaudeBackend's EditShell shape. And `--deny-tool write` cannot carry the
    // Shell rung alone: `copilot help permissions` excludes shell invocations from `write`, so a
    // redirection still writes, hence `--mode plan` there too.
    private static List<string> PermissionArgs(PermissionPolicy permission) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ["--allow-all-tools", "--mode", "plan", "--deny-tool", "write", "--deny-tool", "shell"],
        PermissionLevel.Shell => ShellArgs(permission.Deny),
        PermissionLevel.Edit => ["--allow-all-tools", "--allow-all-paths", "--deny-tool", "shell"],
        PermissionLevel.EditShell => EditShellArgs(permission.Deny),
        PermissionLevel.Full => ["--allow-all"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    // ⚠ Not proven airtight: "no write by any route" rests on plan mode's command-string analyser,
    // not on the permission layer. Measured 2026-09-22 — it flagged `tee leak2.txt` but *not* an
    // `open(...,'w')` inside a `python3 -c` string in the same command; nothing leaked, because the
    // compound was denied for the `tee`. NOTES.md "`shell` rung: what plan mode actually blocks".
    private static List<string> ShellArgs(string[] deny)
    {
        List<string> args = ["--allow-all-tools", "--mode", "plan", "--deny-tool", "write"];
        AddDenyPatterns(args, deny);
        return args;
    }

    private static List<string> EditShellArgs(string[] deny)
    {
        List<string> args = ["--allow-all-tools", "--allow-all-paths"];
        AddDenyPatterns(args, deny);
        return args;
    }

    // `shell(git push)` is `copilot --help`'s own example, and `copilot help permissions` confirms
    // the argument is matched against the first-level subcommand — exactly, unless the pattern ends
    // in `:*`, so a deny entry stops the command it names and not its arguments.
    private static void AddDenyPatterns(List<string> args, string[] deny)
    {
        foreach (string pattern in deny)
            args.AddRange(["--deny-tool", $"shell({pattern})"]);
    }

    // `description` is the one required key (`name` falls back to the file stem); `model`, `tools`,
    // `infer` and `skills` are the rest of the accepted set, and everything else is warned about and
    // ignored — measured 2026-09-22, see NOTES.md "The copilot backend, validated against a real
    // install". Quoted because an unquoted `: ` in a description is a YAML parse error there.
    private static string BuildAgentFile(string agentName, string systemPrompt) =>
        $"""
        ---
        name: {agentName}
        description: "Claustrum-rendered role, do not edit by hand."
        ---
        {systemPrompt}
        """;

    private static string? StringProperty(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Every session event but the CLI's own closing `result` line nests its payload under `data`.
    private static string? DataString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            return null;

        return StringProperty(data, propertyName) is { Length: > 0 } value ? value : null;
    }
}
