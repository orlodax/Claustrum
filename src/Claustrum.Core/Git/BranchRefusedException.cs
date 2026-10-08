namespace Claustrum.Core.Git;

// `run --branch` (#63) could not get a worktree on its branch: another checkout holds it, a finished
// job's worktree would not come off, or `git worktree add` itself said no. The message is the receipt's
// `error`: DelegateEngine turns it into a `status: failed` RunResult, never an exit-2 throw (F11).
public sealed class BranchRefusedException(string message) : Exception(message);
