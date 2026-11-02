namespace Claustrum.Core.Backends;

// #143: what a `readonly` role may run, the same list on every backend that can allow-list a shell (claude's `Bash(…)`, opencode's
// `bash` patterns). `git rev-parse` is how a reviewer in a linked worktree finds git's metadata, which read_dirs now let it read.
internal static class ReadOnlyShell
{
    public static readonly string[] Commands = ["git diff*", "git log*", "git show*", "git status*", "git rev-parse*", "gh pr *", "gh issue *"];

    // opencode only, denied after the allows (its last match wins): it has no plan mode under readonly, and its shell resource is the
    // whole command, redirect included, so `git diff > x` and `git log --output=x` wrote files there (measured 2026-11-02, 2.0.30).
    // `gh pr checkout` rewrites the worktree. NOTES.md "opencode readonly gets a read-only git shell".
    public static readonly string[] OpencodeWriteGuards = ["*>*", "*--output*", "gh pr checkout*"];
}
