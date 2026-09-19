using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Cursor;

// docs/PLAN.md §A3 "cursor" row — entirely unconfirmed, unlike opencode/copilot: Cursor's CLI ships
// as a standalone installer (`curl https://cursor.com/install -fsS | bash`), not an npm package (the
// `cursor-agent` package on npm is an unrelated third-party tool — see NOTES.md "The opencode
// backend"), so it could not be installed or probed in this environment at all. Every detail here —
// the binary name `cursor-agent`, the argv shape, the JSON result field names, the permission
// mapping — is taken directly from docs/PLAN.md's own best-effort table with no live verification,
// same status the plan itself assigns this backend ("Cursor CLI cannot be smoke-tested on this
// machine ... its backend ships behind fixtures + a teammate's validation").
public sealed class CursorBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    public string Name => "cursor";

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync("cursor-agent", ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        string systemPrompt = File.ReadAllText(run.SystemPromptFilePath);
        string prompt = BuildPrompt(systemPrompt, run.Brief, run.Role.Permission.Level, run.Role.Permission.Deny);
        EnsureArgumentFits(prompt);

        List<string> args = ["-p", "--output-format", "json", "--model", run.Role.Model];
        args.AddRange(PermissionArgs(run.Role.Permission));
        args.AddRange(["--workspace", run.Cwd]);
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--resume", resumeSession]);
        args.Add(prompt);

        return new ProcessSpec("cursor-agent", [.. args], run.Cwd, run.Env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // docs/PLAN.md §A3: "json result object ... else text" — cursor-agent's own fallback for
            // a non-JSON stdout (e.g. --output-format wasn't honoured for some reason) is plain text.
            return new ParsedOutput(trimmed, null, null, null, [], null, IsError: exitCode != 0);
        }

        using (document)
        {
            JsonElement root = document.RootElement.Clone();
            string finalMessage = root.TryGetProperty("result", out JsonElement resultProp) ? resultProp.GetString() ?? "" : trimmed;
            string? sessionId = root.TryGetProperty("session_id", out JsonElement sessionProp) ? sessionProp.GetString() : null;
            Usage? usage = root.TryGetProperty("usage", out JsonElement usageProp) ? ParseUsage(usageProp) : null;
            bool isError = exitCode != 0 || (root.TryGetProperty("is_error", out JsonElement errorProp) && errorProp.ValueKind == JsonValueKind.True);

            return new ParsedOutput(finalMessage, sessionId, null, usage, [], root, isError);
        }
    }

    // execve caps a SINGLE argument at 128 KiB on Linux (MAX_ARG_STRLEN) and the whole block at
    // ~32 KiB on Windows; cursor-agent is the one backend that has to put the rendered role body AND
    // the brief in one argv element, so a long diff in `## Diff` hits that ceiling as a bare spawn
    // failure with nothing pointing at the cause (review finding). Refuse early, by name, instead.
    private const int MaxPromptBytes = 96 * 1024;

    private static void EnsureArgumentFits(string prompt)
    {
        int bytes = Encoding.UTF8.GetByteCount(prompt);
        if (bytes > MaxPromptBytes)
            throw new InvalidOperationException(
                $"cursor backend: system prompt + brief is {bytes / 1024}KB, over the {MaxPromptBytes / 1024}KB "
                + "single-argument limit (cursor-agent has no prompt-file flag, unlike claude/opencode/copilot) — "
                + "shorten the brief, or point `## Diff` at a command that reproduces the diff instead of pasting it");
    }

    // No native deny mechanism (docs/PLAN.md §A3: "the deny list is appended to the system prompt as
    // a hard rule and doctor marks it advisory") — cursor-agent has no per-command allow/deny flag the
    // way claude/opencode/copilot do, so this is enforcement by instruction, not by the process.
    private static string BuildPrompt(string systemPrompt, string brief, PermissionLevel level, string[] deny)
    {
        StringBuilder prompt = new(systemPrompt);

        // ReadOnly has no flag behind it here (see PermissionArgs), so the whole level is carried by
        // this rule. Stated first and absolutely, ahead of the per-pattern deny list.
        if (level == PermissionLevel.ReadOnly)
            prompt.Append("\n\n## Hard rules (never violate)\n")
                .Append("- This run is READ-ONLY: never create, edit or delete a file, and never run a command that changes anything.\n")
                .Append("- Report what you found; if a fix is needed, describe it instead of applying it.\n");
        else if (deny.Length > 0)
            prompt.Append("\n\n## Hard rules (never violate)\n");

        foreach (string pattern in deny)
            prompt.Append("- Never run: ").Append(pattern).Append('\n');

        prompt.Append("\n\n# Task\n").Append(brief);
        return prompt.ToString();
    }

    // ReadOnly diverges from docs/PLAN.md §A3's cursor column ("--mode ask (no -f)") for the reason
    // CopilotBackend's own table diverges: `-p` is headless and ProcessRunner closes the child's
    // stdin, so an approval prompt nobody can answer is a guaranteed stall until the run's --timeout
    // kills it — and readonly is the level the most likely cursor role (code-reviewer) uses. cursor
    // has no native deny mechanism at any level, so nothing is actually given up by passing -f: the
    // plan already routes cursor's enforcement through the prompt ("doctor marks it advisory"), and
    // BuildPrompt states the read-only rule there. See NOTES.md "The cursor backend".
    private static List<string> PermissionArgs(PermissionPolicy permission) => permission.Level switch
    {
        PermissionLevel.ReadOnly => ["-f"],
        PermissionLevel.Shell => ["-f"],
        PermissionLevel.Edit => ["-f"],
        PermissionLevel.EditShell => ["-f"],
        PermissionLevel.Full => ["-f", "--sandbox", "disabled"],
        _ => throw new ArgumentOutOfRangeException(nameof(permission)),
    };

    private static Usage ParseUsage(JsonElement usage) => new(
        TryGetInt(usage, "input_tokens"),
        TryGetInt(usage, "output_tokens"),
        null,
        null);

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;
}
