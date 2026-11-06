using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Codex;

// OpenAI's Codex CLI, headless: `codex exec --json`, measured against `codex-cli 0.166.0` on 2026-11-04 with an API-key login
// (NOTES.md "The codex backend, validated against a real install" logs every command). The prompt goes in on stdin (`-`), the
// role body prefixed onto the brief as cursor's is: codex has no system-prompt flag, and it reads the repo's AGENTS.md itself.
public sealed class CodexBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    private const string DenyAdvisory =
        """deny list is enforced by prompt only: codex exec has no per-run deny flag (NOTES.md "The codex backend, validated against a real install")""";

    // The one refusal codex gives neither a status nor a type for (tests/fixtures/codex/model-not-found.jsonl): matched whole, never searched.
    private const string ModelNotFoundStart = "The model `";
    private const string ModelNotFoundEnd = "` does not exist or you do not have access to it.";

    // Codex's own fixed prefixes before an HTTP status: `unexpected status 401 Unauthorized: …` (auth-401.jsonl), and the retry
    // limit's `exceeded retry limit, last status: 429 Too Many Requests` (codex 0.166 `RetryLimitReachedError`, read in its source).
    private static readonly string[] statusPrefixes = ["unexpected status ", "exceeded retry limit, last status: "];

    // Codex 0.166 turns a 429, a 5xx or a full model into these fixed sentences (protocol/src/error.rs), never a status. Matched as
    // codex writes them, from the start; the usage-limit ones go on with a plan- or reset-specific tail.
    private static readonly (string Start, ErrorKind Kind)[] fixedRefusals =
    [
        ("Quota exceeded. Check your plan and billing details.", ErrorKind.ProviderQuota),
        ("You\u2019ve hit your usage limit", ErrorKind.ProviderQuota),
        ("Your workspace is out of credits.", ErrorKind.ProviderQuota),
        ("You hit your spend cap set ", ErrorKind.ProviderQuota),
        ("To use Codex with your ChatGPT plan, upgrade to Plus", ErrorKind.ProviderQuota),
        ("rate limit exceeded: ", ErrorKind.ProviderQuota),
        ("Selected model is at capacity. Please try a different model.", ErrorKind.ProviderTransport),
        ("We\u2019re currently experiencing high demand, which may cause temporary errors.", ErrorKind.ProviderTransport),
        ("Flex capacity unavailable.", ErrorKind.ProviderTransport),
        ("Policy-restricted capacity is unavailable.", ErrorKind.ProviderTransport),
    ];

    private static readonly string[] toolItemTypes = ["command_execution", "file_change", "mcp_tool_call", "web_search"];

    public string Name => "codex";

    // Both sandboxes read the whole disk (measured: a file under ~/.cache read at read-only and workspace-write), so a read dir
    // needs no flag; `--add-dir` would make it writable, which is the opposite of a read dir.
    public bool HonoursReadDirs => true;

    public IStreamFormatter? CreateStreamFormatter(string cwd) => new CodexStreamFormatter();

    public async Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken)
    {
        Doctor doctor = await VersionProbe.RunAsync("codex", ["--version"], config, platform, cancellationToken, detectTimeout);
        return doctor with { Advisories = [DenyAdvisory] };
    }

    public ProcessSpec Build(ResolvedRun run)
    {
        string systemPrompt = File.ReadAllText(run.SystemPromptFilePath);
        string prompt = BuildPrompt(systemPrompt, run.Brief, run.Role.Permission.Level, run.Role.Permission.Deny);

        // `resume` takes no `-s`/`-C`, and without a sandbox it falls back to workspace-write even for a thread begun read-only
        // (measured: a resumed read-only thread wrote r1.txt). So the sandbox always goes in as `-c sandbox_mode`, which both honour,
        // and the workspace is the process cwd, which both use.
        List<string> args = ["exec"];
        if (run.ResumeSession is { Length: > 0 })
            args.Add("resume");
        // Measured: a `.codex/rules` allow rule ran `touch` outside the read-only sandbox, and a trusted repo's `.codex/config.toml`
        // was loaded (codex trusts a repo itself after a writing run). --ignore-rules and --ignore-user-config stop both; auth stays.
        args.AddRange(["--json", "--skip-git-repo-check", "--ignore-rules", "--ignore-user-config", "-m", run.Role.Model]);
        args.AddRange(PermissionArgs(run.Role.Permission.Level));
        if (!string.IsNullOrEmpty(run.Role.Effort))
            args.AddRange(["-c", $"model_reasoning_effort=\"{run.Role.Effort}\""]);
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.Add(resumeSession);
        args.Add("-");

        return new ProcessSpec("codex", [.. args], run.Cwd, run.Env, [], prompt);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        // Outside a git repo without the check flag, and any startup refusal: stderr only, empty stdout, exit 1.
        if (stdout.Trim().Length == 0)
            return new ParsedOutput(stderr.Trim(), null, null, null, [], null, IsError: exitCode != 0);

        string? sessionId = null;
        string finalMessage = "";
        Usage? usage = null;
        string? failure = null;
        int toolCalls = 0;

        foreach (string rawLine in stdout.Split('\n'))
        {
            if (Event(rawLine) is not { } root)
                continue;

            switch (StringOf(root, "type"))
            {
                case "thread.started":
                    sessionId = StringOf(root, "thread_id") ?? sessionId;
                    break;
                case "item.completed" when ObjectOf(root, "item") is { } item:
                    string? itemType = StringOf(item, "type");
                    if (itemType == "agent_message")
                        finalMessage = StringOf(item, "text") ?? finalMessage;
                    else if (toolItemTypes.Contains(itemType))
                        toolCalls++;
                    break;
                case "turn.completed" when ObjectOf(root, "usage") is { } usageObject:
                    usage = ParseUsage(usageObject);
                    break;
                case "turn.failed":
                    failure = ObjectOf(root, "error") is { } error ? StringOf(error, "message") : null;
                    failure ??= "codex reported turn.failed with no message";
                    break;
                default:
                    break;
            }
        }

        // A top-level `error` event is a retry notice ("Reconnecting... 2/5"), never the outcome: only turn.failed ends a run badly.
        if (failure is not null)
        {
            return new ParsedOutput(failure, sessionId, null, usage, [], null, IsError: true,
                ErrorKind: Classify(failure), ErrorDetail: failure, ToolCallsCompleted: toolCalls);
        }

        // No cost anywhere in the stream (turn.completed carries tokens only), so CostUsd is null by measurement, as cursor's.
        return new ParsedOutput(finalMessage, sessionId, null, usage, [], null, IsError: exitCode != 0, ToolCallsCompleted: toolCalls);
    }

    // A refusal's message is the provider's JSON body (status and error.type, the 400 fixture), a codex status prefix, one of codex's
    // fixed refusal sentences, or the model-not-found sentence matched whole; anything else is Other, which is never retried.
    private static ErrorKind Classify(string message)
    {
        if (Event(message) is { } body)
        {
            int? status = IntOf(body, "status");
            string? type = ObjectOf(body, "error") is { } error ? StringOf(error, "type") : null;
            return ProviderErrors.Classify(status, type);
        }

        foreach (string prefix in statusPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal)
                && message.Length >= prefix.Length + 3
                && int.TryParse(message.AsSpan(prefix.Length, 3), out int httpStatus))
            {
                return ProviderErrors.Classify(httpStatus, null);
            }
        }

        if (fixedRefusals.FirstOrDefault(refusal => message.StartsWith(refusal.Start, StringComparison.Ordinal)) is { Start: not null } known)
            return known.Kind;

        return message.StartsWith(ModelNotFoundStart, StringComparison.Ordinal) && message.EndsWith(ModelNotFoundEnd, StringComparison.Ordinal)
            ? ErrorKind.ModelNotFound
            : ErrorKind.Other;
    }

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

    // What the sandbox does not say. read-only is a real fence (a write came back "Read-only file system"); the rule tells the model
    // to report the fix instead of trying. Shell and Edit share workspace-write with EditShell: their halves are by instruction.
    private static string[] LevelRules(PermissionLevel level) => level switch
    {
        PermissionLevel.ReadOnly =>
        [
            "This run is READ-ONLY: never run a command that changes anything, and report a needed fix instead of applying it.",
        ],
        PermissionLevel.Shell => ["Never edit a file yourself: run commands and report, nothing else."],
        PermissionLevel.Edit => ["Never run a shell command: edit files and report, nothing else."],
        _ => [],
    };

    // Measured: both sandboxes cut the network ("Could not resolve host"); `sandbox_workspace_write.network_access=true` restores it
    // (curl 200), so the shell levels can fetch, and `dotnet build` ran from a warm ~/.nuget. Writable: the cwd, /tmp, $TMPDIR, never
    // $HOME's caches or a linked worktree's git dir. Read-only has no network key (`--strict-config` refuses `sandbox_read_only`).
    private static string[] PermissionArgs(PermissionLevel level) => level switch
    {
        PermissionLevel.ReadOnly => ["-c", "sandbox_mode=\"read-only\""],
        PermissionLevel.Edit => ["-c", "sandbox_mode=\"workspace-write\""],
        PermissionLevel.Shell or PermissionLevel.EditShell => ["-c", "sandbox_mode=\"workspace-write\"", "-c", "sandbox_workspace_write.network_access=true"],
        PermissionLevel.Full => ["-c", "sandbox_mode=\"danger-full-access\""],
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    // OpenAI's accounting: `input_tokens` includes `cached_input_tokens`, unlike claude's, and `cache_write_input_tokens` is codex's own.
    private static Usage ParseUsage(JsonElement usage) => new(
        IntOf(usage, "input_tokens"),
        IntOf(usage, "output_tokens"),
        IntOf(usage, "cached_input_tokens"),
        IntOf(usage, "cache_write_input_tokens"));

    internal static JsonElement? Event(string rawLine)
    {
        string line = rawLine.Trim();
        if (!line.StartsWith('{'))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement? ObjectOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;

    internal static string? StringOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
}
