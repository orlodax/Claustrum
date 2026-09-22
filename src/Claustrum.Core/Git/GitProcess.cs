using Claustrum.Core.Process;

namespace Claustrum.Core.Git;

// Shared `git` child-process invocation for WorktreeSnapshot and JobWorktree: CommandProcess with
// this repo's git timeout bound to it. The stdin and timeout fixes of NOTES.md "MCP child stdin
// inheritance hung git" live in CommandProcess now, so `gh` (Coordination/GhIssueSource.cs) gets
// them too instead of re-deriving them.
public static class GitProcess
{
    // Defense-in-depth on top of CommandProcess's stdin fix: with a child's stdin properly closed
    // rather than inherited, this should never fire, but a stuck git must not be able to wedge a
    // tool call forever the way it did before that fix.
    private static readonly TimeSpan gitTimeout = TimeSpan.FromSeconds(30);

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string cwd, string[] args, CancellationToken cancellationToken) =>
        await CommandProcess.RunAsync("git", cwd, args, gitTimeout, cancellationToken);
}
