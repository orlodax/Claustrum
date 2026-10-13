namespace Claustrum.Core.Git;

// #106: a git call of freeing a finished job's worktree died of the terminal's SIGINT/SIGTERM (it shares Claustrum's
// process group) before the caller's own token was cancelled. Still a cancel for the callers that filter on one;
// nothing was lost: the worktree is intact, or renamed aside and pruned by the next free (JobWorktree.TryFreeAsync).
public sealed class WorktreeInterruptedException(string message) : OperationCanceledException(message);
