namespace Claustrum.Core.Git;

// What JobWorktree.VerifyAsync found instead of the job's worktree on its branch (F6, R5, T3).
// OwnWorktree: the path is still the top of a working tree, so it is safe to read but not to commit — its HEAD is
// elsewhere (`Reason` "a detached HEAD" or the `refs/heads/…` it is on), or it is on its branch with Operation in
// progress ("a merge", "a rebase", "a git am"; `Reason` "a merge is in progress"), or with UnresolvedConflicts
// unmerged paths and no operation (`Reason` "unresolved conflicts (2 paths)"; #74 round 5). Otherwise git would
// read the main checkout there, or nothing at all, and `Reason` says why.
public sealed record WorktreeMismatch(string Reason, bool OwnWorktree, string? Operation = null, int UnresolvedConflicts = 0);
