namespace Claustrum.Core.Git;

// What JobWorktree.VerifyAsync found instead of the job's worktree on its branch (F6, R5, T3).
// OwnWorktree: the path is still the top of a working tree, only its HEAD is elsewhere — `Reason` is then
// "a detached HEAD" or the `refs/heads/…` it is on — so it is safe to read but not to commit. Otherwise
// git would read the main checkout there, or nothing at all, and `Reason` says why.
public sealed record WorktreeMismatch(string Reason, bool OwnWorktree);
