using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// The four harness syncs behind one call, and where each puts its agent files, so a test written once
// runs for claude, opencode, copilot and cursor without four copies of the same arrange step.
internal static class HarnessSyncs
{
    public static readonly string[] All = ["claude", "opencode", "copilot", "cursor"];

    public static SyncResult Run(
        string harness, RoleLibrary library, string home, string cwd, IReadOnlyList<string>? roles = null,
        bool global = false, bool force = false, SyncMode mode = SyncMode.Write, string? globalRoot = null)
    {
        RoleRenderer renderer = new(library);
        return harness switch
        {
            "claude" => new ClaudeSync(library, renderer, home).Sync(cwd, roles, global, force, mode, globalRoot: globalRoot),
            "opencode" => new OpencodeSync(library, renderer, home).Sync(cwd, roles, global, force, mode, globalRoot),
            "copilot" => new CopilotSync(library, renderer, home).Sync(cwd, roles, global, force, mode, globalRoot),
            "cursor" => new CursorSync(library, renderer, home).Sync(cwd, roles, global, force, mode, globalRoot: globalRoot),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a harness with a sync"),
        };
    }

    /// <summary>What a repo-mode sync writes under: `.claude`, `.opencode`, `.github`, `.cursor`.</summary>
    public static string RepoRoot(string harness, string cwd) => Path.Combine(cwd, harness switch
    {
        "claude" => ".claude",
        "opencode" => ".opencode",
        "copilot" => ".github",
        "cursor" => ".cursor",
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a harness with a sync"),
    });

    /// <summary>The harness's own default global root under <paramref name="home"/>, spelled by its sync.</summary>
    public static string DefaultGlobalRoot(string harness, string home) => harness switch
    {
        "claude" => ClaudeSync.DefaultGlobalRoot(home),
        "opencode" => OpencodeSync.DefaultGlobalRoot(home),
        "copilot" => CopilotSync.DefaultGlobalRoot(home),
        "cursor" => CursorSync.DefaultGlobalRoot(home),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a harness with a sync"),
    };

    public static string AgentDirectory(string harness, string root) => Path.Combine(root, harness == "opencode" ? "agent" : "agents");

    public static string AgentFile(string harness, string root, string name) =>
        Path.Combine(AgentDirectory(harness, root), harness == "copilot" ? $"{name}.agent.md" : $"{name}.md");

    /// <summary>The file carrying the shared `/claustrum` skill or command, which each harness spells differently.</summary>
    public static string SkillFile(string harness, string root) => harness == "opencode"
        ? Path.Combine(root, "command", "claustrum.md")
        : Path.Combine(root, "skills", "claustrum", "SKILL.md");
}
