namespace Claustrum.Core.Git;

// What an isolated run's receipt says about its branch once the runner has committed its leftovers
// (JobWorktree.CommitRunAsync): `commit` — the branch tip when the run moved it — the changes to report
// as `changed_files`/`diff`, and any warnings for a commit or a delta git would not give.
public sealed record BranchReceipt(string? Commit, SnapshotDiff Changes, string[] Warnings);
