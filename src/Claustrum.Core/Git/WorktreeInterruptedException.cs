namespace Claustrum.Core.Git;

// #106: `git worktree remove` died of the terminal's SIGINT/SIGTERM (it shares Claustrum's process group) before
// the caller's own token was cancelled. Still a cancel for the callers that filter on one; the message names
// what the remove left or finished.
public sealed class WorktreeInterruptedException(string message) : OperationCanceledException(message);
