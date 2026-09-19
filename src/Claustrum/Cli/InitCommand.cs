using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§D5 `claustrum init [--all]`: scaffolds .claustrum/{casts,briefs,worktrees},
// writes claustrum.json if one is not already there, syncs the harnesses this repo already uses (or
// every supported one with --all), and appends a short pointer to an existing AGENTS.md. Never
// creates AGENTS.md itself and never writes CLAUDE.md at all (docs/PLAN.md's own explicit rule) —
// claustrum.json, .gitignore, and the .claustrum/ scaffold are the only new files this can create in
// a repo that has none of them yet.
public static class InitCommand
{
    // .claustrum/casts/ is committable (docs/PLAN.md §D1); worktrees/, briefs/ and locks/ are not
    // (docs/PLAN.md §D5 "the last two git-ignored") — briefs/ doesn't exist as a feature yet, but the
    // directory and its ignore rule are scaffolded now so a later feature has nothing left to wire up.
    // locks/ matters most: RoleConcurrencyGate's slot files are untracked, and WorktreeSnapshot runs
    // `git status --untracked-files=all`, so leaving them un-ignored puts phantom lock files in every
    // subsequent job's changed_files/diff (review finding — this repo's own .gitignore already has it).
    private const string GitignoreBlock = """
        # Claustrum: per-job worktrees, drafted briefs and concurrency locks (casts/ is committable, these are not).
        .claustrum/worktrees/
        .claustrum/briefs/
        .claustrum/locks/
        """;

    // Every line GitignoreBlock contributes, checked individually: keying idempotency on one line
    // meant a repo initialised by an earlier version never received a rule added later.
    private static readonly string[] gitignoreEntries =
        [".claustrum/worktrees/", ".claustrum/briefs/", ".claustrum/locks/"];

    private const string AgentsMdMarker = "## Claustrum delegation";

    private const string AgentsMdBlock = """

        ## Claustrum delegation
        This repo uses Claustrum for harness-neutral agent delegation. Run `claustrum cast questions --json`
        once to set up a cast, then `claustrum run <role> --brief-file <path>` (or the `delegate` MCP tool)
        to delegate a task. See `.claustrum/casts/default.json` and https://github.com/orlodax/Claustrum.
        """;

    public static Command Build()
    {
        Option<bool> all = new("--all") { Description = "Sync every supported harness, not just the ones this repo already uses." };
        Command command = new("init", "Scaffold Claustrum in the current repo: claustrum.json, .claustrum/, and sync for the harnesses in use.") { all };
        command.SetAction(parseResult => Execute(parseResult.GetValue(all)));
        return command;
    }

    private static int Execute(bool all)
    {
        string cwd = Environment.CurrentDirectory;

        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "briefs"));
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "worktrees"));

        bool wroteConfig = WriteConfigIfAbsent(cwd);
        bool updatedGitignore = EnsureGitignoreEntries(cwd);

        // "claude" is always synced, --all or not: it is the only harness whose Sync also merges the
        // claustrum MCP server into .mcp.json/.vscode/mcp.json (McpConfigSync is internal to
        // Claustrum.Roles, wired only through ClaudeSync.Sync today), and its own agent files are
        // harmless even in a repo that has not adopted Claude Code.
        string[] harnesses = all ? ["claude", "opencode", "copilot"] : DetectHarnesses(cwd);

        Console.WriteLine($"claustrum.json: {(wroteConfig ? "written" : "already present, left unchanged")}");
        Console.WriteLine($".gitignore: {(updatedGitignore ? "updated" : "already up to date")}");
        foreach (string harness in harnesses)
            SyncHarness(harness, cwd);

        if (Directory.Exists(Path.Combine(cwd, ".cursor")))
            Console.WriteLine("detected .cursor/, but cursor has no sync support yet (fixture-only backend — see NOTES.md)");

        bool appendedAgentsMd = AppendAgentsMdPointer(cwd);
        Console.WriteLine($"AGENTS.md: {(appendedAgentsMd ? "pointer appended" : "unchanged")}");

        return ExitCodes.Ok;
    }

    private static string[] DetectHarnesses(string cwd)
    {
        List<string> harnesses = ["claude"];
        if (Directory.Exists(Path.Combine(cwd, ".opencode"))
            || File.Exists(Path.Combine(cwd, "opencode.json"))
            || File.Exists(Path.Combine(cwd, "opencode.jsonc")))
            harnesses.Add("opencode");
        if (Directory.Exists(Path.Combine(cwd, ".github")))
            harnesses.Add("copilot");

        return [.. harnesses];
    }

    private static void SyncHarness(string harness, string cwd)
    {
        SyncResult result = harness switch
        {
            "claude" => new ClaudeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd),
            "opencode" => new OpencodeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd),
            "copilot" => new CopilotSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a supported harness"),
        };

        Console.WriteLine($"{harness}: {result.Written.Count} written, {result.Skipped.Count} skipped, {result.Foreign.Count} foreign");
    }

    private static bool WriteConfigIfAbsent(string cwd)
    {
        string path = Path.Combine(cwd, "claustrum.json");
        if (File.Exists(path))
            return false;

        // docs/PLAN.md §A5: "default model aliases incl. cheap-coding -> opencode:openrouter/
        // deepseek/deepseek-v4-flash, frontier-coding -> claude:opus" — frontier-coding already
        // matches Core's own built-in default (Config.BuiltInDefaults); writing it here makes it a
        // visible, editable starting point rather than an invisible fallback. cheap-coding is
        // deliberately overridden to the cross-harness example so a fresh repo demonstrates using a
        // second backend, not just claude for everything.
        //
        // Built with Utf8JsonWriter directly, not ClaustrumJsonContext.Default.ConfigDocument:
        // that shared context has no WriteIndented (it also emits RunResult's machine-readable
        // one-liner, whose null fields are part of the documented --json shape), so serializing the
        // full ConfigDocument through it would litter this hand-editable file with every other field
        // as an explicit "roles": null, "backends": null, etc.
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("models");
            writer.WriteString("frontier-coding", "claude:opus");
            writer.WriteString("cheap-coding", "opencode:openrouter/deepseek/deepseek-v4-flash");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
        return true;
    }

    private static bool EnsureGitignoreEntries(string cwd)
    {
        string path = Path.Combine(cwd, ".gitignore");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, GitignoreBlock + "\n");
            return true;
        }

        string existing = File.ReadAllText(path);
        string[] missing = [.. gitignoreEntries.Where(entry => !existing.Contains(entry, StringComparison.Ordinal))];
        if (missing.Length == 0)
            return false;

        // Only what is actually missing is appended, so a repo initialised before a rule existed
        // gains that one line instead of a second copy of the whole block.
        string block = missing.Length == gitignoreEntries.Length
            ? GitignoreBlock
            : "# Claustrum (added by `claustrum init`).\n" + string.Join('\n', missing);

        File.AppendAllText(path, (existing.EndsWith('\n') ? "" : "\n") + "\n" + block + "\n");
        return true;
    }

    private static bool AppendAgentsMdPointer(string cwd)
    {
        string path = Path.Combine(cwd, "AGENTS.md");
        if (!File.Exists(path))
            return false;

        string existing = File.ReadAllText(path);
        if (existing.Contains(AgentsMdMarker, StringComparison.Ordinal))
            return false;

        File.AppendAllText(path, (existing.EndsWith('\n') ? "" : "\n") + AgentsMdBlock + "\n");
        return true;
    }
}
