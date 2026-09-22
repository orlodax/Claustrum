namespace Claustrum.Roles.Sync;

/// <summary>
/// docs/PLAN.md §B4's fourth <c>--global</c> target: the Claude desktop app's own
/// <c>claude_desktop_config.json</c> (<paramref name="ConfigPath"/>) and the absolute
/// <paramref name="BinaryPath"/> to register as the <c>claustrum</c> server's command — the desktop
/// app launches the server from a GUI process whose <c>PATH</c> rarely holds the install directory,
/// so a bare <c>claustrum</c> would not resolve. Both are resolved by the caller (the CLI, from
/// <c>IPlatform</c> and <c>Environment.ProcessPath</c>) rather than read here, so a test can point
/// the merge at a temp file, and so Roles reads no environment of its own.
/// </summary>
public sealed record ClaudeDesktopTarget(string ConfigPath, string BinaryPath);
