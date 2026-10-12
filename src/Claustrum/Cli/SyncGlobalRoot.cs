using Claustrum.Core.Platform;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// Where `sync --global` writes for each harness: the directory the harness itself reads, which an
// environment variable can move (issue #83, NOTES.md "`sync --global` roots follow each harness's
// config-dir variable"). The lookup lives here, not in Claustrum.Roles, for ClaudeDesktopConfig's
// reason: the environment is read in Cli through IPlatform, and the syncs take the root as a value.
internal static class SyncGlobalRoot
{
    /// <summary>
    /// The global root for <paramref name="harness"/> and the short phrase naming what decided it,
    /// for <c>sync --global</c> to print. An empty or whitespace variable counts as unset.
    /// </summary>
    public static (string Path, string Reason) Resolve(IPlatform platform, string harness)
    {
        string home = platform.HomeDirectory;
        switch (harness)
        {
            case "claude":
                return Env(platform, "CLAUDE_CONFIG_DIR") is { } claudeDir
                    ? (claudeDir, "CLAUDE_CONFIG_DIR")
                    : (ClaudeSync.DefaultGlobalRoot(home), "default; CLAUDE_CONFIG_DIR not set");
            case "opencode":
                if (Env(platform, "OPENCODE_CONFIG_DIR") is { } opencodeDir)
                    return (opencodeDir, "OPENCODE_CONFIG_DIR");

                return Env(platform, "XDG_CONFIG_HOME") is { } xdgHome
                    ? (Path.Combine(xdgHome, "opencode"), "XDG_CONFIG_HOME")
                    : (OpencodeSync.DefaultGlobalRoot(home), "default; OPENCODE_CONFIG_DIR and XDG_CONFIG_HOME not set");
            case "copilot":
                return Env(platform, "COPILOT_HOME") is { } copilotHome
                    ? (copilotHome, "COPILOT_HOME")
                    : (CopilotSync.DefaultGlobalRoot(home), "default; COPILOT_HOME not set");
            // cursor-agent hard-codes ~/.cursor for agents, skills and mcp.json (measured 2026-10-12,
            // 2026.09.18): honouring CURSOR_CONFIG_DIR here would write files it never loads.
            case "cursor":
                return (CursorSync.DefaultGlobalRoot(home), "cursor-agent reads agents, skills and mcp.json from ~/.cursor; CURSOR_CONFIG_DIR moves only cli-config.json");
            default:
                throw new ArgumentOutOfRangeException(nameof(harness), harness, "not in supportedHarnesses");
        }
    }

    private static string? Env(IPlatform platform, string name) =>
        platform.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value ? value : null;
}
