using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.IO;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §A5/§D5 `claustrum init [--all]`: scaffolds .claustrum/{casts,briefs,worktrees},
// writes claustrum.json if one is not already there, syncs the harnesses this repo already uses (or
// every supported one with --all), and appends a short pointer to an existing AGENTS.md. Never
// creates AGENTS.md itself and never writes CLAUDE.md at all (docs/PLAN.md's own explicit rule) —
// claustrum.json, .gitignore, and the .claustrum/ scaffold are the only new files this can create in
// a repo that has none of them yet. Every write is fenced to the cwd first: a committed link that
// leaves it is refused, not followed, and init exits 2 at the end (issue #98).
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

    private static readonly string[] scaffoldDirectories = ["casts", "briefs", "worktrees"];

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
        // The cwd, not the git root, as in `sync` (NOTES.md "Why the fence is the cwd and not the git root").
        SyncRoot root = SyncRoot.Repo(cwd);

        // Before the first write, so a malformed claustrum.json or an alias cycle is exit 2 with nothing created (#53 review).
        Config config = LoadConfig(cwd);

        string? scaffoldEscape = Scaffold(root, cwd);
        if (scaffoldEscape is not null)
            Console.WriteLine($".claustrum/: {RefusedStatus(scaffoldEscape)}");

        string configPath = Path.Combine(cwd, "claustrum.json");
        string? configEscape = Refusal(root, configPath);
        string? configUnwritable = null;
        bool wroteConfig = configEscape is null && WriteConfigIfAbsent(configPath, out configUnwritable);
        if (wroteConfig)
            config = LoadConfig(cwd);

        string gitignorePath = Path.Combine(cwd, ".gitignore");
        string? gitignoreEscape = Refusal(root, gitignorePath);
        string? gitignoreUnreadable = null;
        bool updatedGitignore = gitignoreEscape is null && EnsureGitignoreEntries(gitignorePath, out gitignoreUnreadable);

        // "claude" is always synced, --all or not: its Sync is the one that merges the claustrum MCP
        // server into .mcp.json/.vscode/mcp.json, which every host that reads those two files picks
        // up (opencode registers itself in opencode.json instead, issue #15), and its own agent files
        // are harmless even in a repo that has not adopted Claude Code.
        string[] harnesses = all ? ["claude", "opencode", "copilot", "cursor"] : DetectHarnesses(cwd);

        Console.WriteLine($"claustrum.json: {Status(configEscape, configUnwritable, wroteConfig ? "written" : "already present, left unchanged")}");
        Console.WriteLine($".gitignore: {Status(gitignoreEscape, gitignoreUnreadable, updatedGitignore ? "updated" : "already up to date")}");
        bool refused = scaffoldEscape is not null || configEscape is not null || configUnwritable is not null || gitignoreEscape is not null || gitignoreUnreadable is not null;
        foreach (string harness in harnesses)
            refused |= SyncHarness(harness, cwd, config);

        string agentsPath = Path.Combine(cwd, "AGENTS.md");
        string? agentsEscape = Refusal(root, agentsPath);
        string? agentsUnreadable = null;
        bool appendedAgentsMd = agentsEscape is null && AppendAgentsMdPointer(agentsPath, out agentsUnreadable);
        Console.WriteLine($"AGENTS.md: {Status(agentsEscape, agentsUnreadable, appendedAgentsMd ? "pointer appended" : "unchanged")}");
        refused |= agentsEscape is not null || agentsUnreadable is not null;

        return refused ? ExitCodes.Usage : ExitCodes.Ok;
    }

    // Creates the three directories, or none: null when they now exist, otherwise the real path of the first
    // one that leaves the repo (`.claustrum` itself linked out makes all three do so), with nothing created.
    private static string? Scaffold(SyncRoot root, string cwd)
    {
        string[] directories = [.. scaffoldDirectories.Select(name => Path.Combine(cwd, ".claustrum", name))];
        string? escapesTo = directories.Select(directory => Refusal(root, directory)).FirstOrDefault(real => real is not null);
        if (escapesTo is not null)
            return escapesTo;

        foreach (string directory in directories)
            Directory.CreateDirectory(directory);

        return null;
    }

    // The real path when `path` resolves outside the repo, else null. Checked on the full path, leaf
    // included, because File.WriteAllText and AppendAllText follow a leaf link, so the bytes land at its target.
    private static string? Refusal(SyncRoot root, string path) => root.Escapes(path) ? SyncRoot.Resolve(path) : null;

    private static string RefusedStatus(string escapesTo) => $"refused (resolves outside the repo → {escapesTo})";

    private static string Status(string? escapesTo, string outcome) => escapesTo is null ? outcome : RefusedStatus(escapesTo);

    // `unreadable` is PlainFile's reason for a file that is not a regular one (a FIFO there would block the read or the write).
    private static string Status(string? escapesTo, string? unreadable, string outcome) =>
        unreadable is null ? Status(escapesTo, outcome) : $"refused ({unreadable})";

    private static string[] DetectHarnesses(string cwd)
    {
        List<string> harnesses = ["claude"];
        if (Directory.Exists(Path.Combine(cwd, ".opencode"))
            || File.Exists(Path.Combine(cwd, "opencode.json"))
            || File.Exists(Path.Combine(cwd, "opencode.jsonc")))
            harnesses.Add("opencode");
        if (Directory.Exists(Path.Combine(cwd, ".github")))
            harnesses.Add("copilot");
        if (Directory.Exists(Path.Combine(cwd, ".cursor")))
            harnesses.Add("cursor");

        return [.. harnesses];
    }

    private static Config LoadConfig(string cwd)
    {
        Config config = Config.Load(AppServices.Platform, cwd);
        SyncSettings.ValidateModels(config, AppServices.RoleLibrary, null, cwd);
        return config;
    }

    // True when sync refused a target for resolving outside the repo (issue #95): init finishes, then exits 2.
    private static bool SyncHarness(string harness, string cwd, Config config)
    {
        SyncResult result = harness switch
        {
            "claude" => new ClaudeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "opencode" => new OpencodeSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "copilot" => new CopilotSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            "cursor" => new CursorSync(AppServices.RoleLibrary, AppServices.RoleRenderer, AppServices.Platform.HomeDirectory).Sync(cwd, config: config),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a supported harness"),
        };

        IReadOnlyDictionary<string, string> refused = result.Refused ?? new Dictionary<string, string>();
        string refusedNote = refused.Count > 0 ? $", {refused.Count} refused (outside the repo, or a pipe, socket or device)" : "";
        Console.WriteLine($"{harness}: {result.Written.Count} written, {result.Skipped.Count} skipped, {result.Foreign.Count} foreign{refusedNote}");
        foreach (KeyValuePair<string, string> entry in refused)
            Console.WriteLine($"  {entry.Key} → {entry.Value}");

        return refused.Count > 0;
    }

    private static bool WriteConfigIfAbsent(string path, out string? unwritable)
    {
        unwritable = null;

        // Present means a regular file: a FIFO or a device there is a refusal, not "already present" (no git root loads it to notice).
        if (File.Exists(path))
        {
            using FileStream? existing = PlainFile.OpenRead(path, out unwritable);
            return false;
        }

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

        // Append, not Create: a file that appeared since the check above is never truncated.
        return PlainFile.TryWriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n", FileMode.Append, out unwritable);
    }

    private static bool EnsureGitignoreEntries(string path, out string? unreadable)
    {
        unreadable = null;
        if (!File.Exists(path))
        {
            return PlainFile.TryWriteAllText(path, GitignoreBlock + "\n", FileMode.Append, out unreadable);
        }

        if (!PlainFile.TryReadAllText(path, out string? existing, out unreadable))
            return false;

        string[] missing = [.. gitignoreEntries.Where(entry => !existing.Contains(entry, StringComparison.Ordinal))];
        if (missing.Length == 0)
            return false;

        // Only what is actually missing is appended, so a repo initialised before a rule existed
        // gains that one line instead of a second copy of the whole block.
        string block = missing.Length == gitignoreEntries.Length
            ? GitignoreBlock
            : "# Claustrum (added by `claustrum init`).\n" + string.Join('\n', missing);

        return PlainFile.TryWriteAllText(path, (existing.EndsWith('\n') ? "" : "\n") + "\n" + block + "\n", FileMode.Append, out unreadable);
    }

    private static bool AppendAgentsMdPointer(string path, out string? unreadable)
    {
        unreadable = null;
        if (!File.Exists(path))
            return false;

        if (!PlainFile.TryReadAllText(path, out string? existing, out unreadable))
            return false;

        if (existing.Contains(AgentsMdMarker, StringComparison.Ordinal))
            return false;

        return PlainFile.TryWriteAllText(path, (existing.EndsWith('\n') ? "" : "\n") + AgentsMdBlock + "\n", FileMode.Append, out unreadable);
    }
}
