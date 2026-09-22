using Claustrum.Core.Platform;
using Claustrum.Roles.Sync;

namespace Claustrum.Cli;

// docs/PLAN.md §B4's fourth `--global` target (issue #24). The path lives here, not in
// Claustrum.Roles, so ClaudeSync stays free of environment reads and a test can hand it a temp file.
internal static class ClaudeDesktopConfig
{
    /// <summary>
    /// The desktop app's config file for this OS, or null with a reason to print: Linux has no Claude
    /// desktop app at all, a Windows session may have no <c>%APPDATA%</c>, the app may not be
    /// installed here, or this process is not the claustrum binary
    /// (<paramref name="binaryUnavailableReason"/>, non-null exactly when
    /// <paramref name="binaryPath"/> is null — see <see cref="ClaustrumBinaryPath.Resolve"/>).
    /// </summary>
    public static (ClaudeDesktopTarget? Target, string? SkipReason) Resolve(
        IPlatform platform, string? binaryPath, string? binaryUnavailableReason = null)
    {
        (string? configDirectory, string? skipReason) = AppDirectory(platform);
        if (configDirectory is null)
            return (null, skipReason);

        // The app owns this directory; creating it from here plants a config no app reads, and
        // `--check` then reports that file stale on every later run (review finding). Its *file* may
        // still be absent — a fresh install has the directory and no config yet, and that one is ours
        // to create.
        if (!Directory.Exists(configDirectory))
            return (null, $"Claude desktop app not found at {configDirectory}");

        if (binaryPath is not { Length: > 0 })
            return (null, binaryUnavailableReason ?? "the running binary's own path is unknown");

        return (new ClaudeDesktopTarget(Path.Combine(configDirectory, "claude_desktop_config.json"), binaryPath), null);
    }

    // The app's own config directory, checked before the binary is: on Linux "there is no such app"
    // is the whole truth, and telling a Linux user to install the release binary instead would send
    // them after nothing.
    private static (string? ConfigDirectory, string? SkipReason) AppDirectory(IPlatform platform) => platform.Os switch
    {
        ClaustrumOs.Windows => platform.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData
            ? (Path.Combine(appData, "Claude"), null)
            : (null, "APPDATA is not set"),
        ClaustrumOs.MacOs => (Path.Combine(platform.HomeDirectory, "Library", "Application Support", "Claude"), null),
        _ => (null, "there is no Claude desktop app on Linux"),
    };
}
