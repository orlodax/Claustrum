namespace Claustrum.Core.Git;

// One entry from `git status --porcelain=v1 -z` (docs/PLAN.md A2). IndexStatus/WorktreeStatus are
// the raw XY letters; OldPath is set for a rename (`R` in either column) or a copy (`C` in the
// index column). ContentHash is a SHA-256 of the worktree file's bytes at capture time (null when
// the worktree side doesn't exist, e.g. deleted) — DiffAsync reports a path when its status code OR
// this hash changed, which is what catches an edit to a file that was already dirty going in
// (NOTES.md "Worktree snapshot").
public sealed record GitStatusEntry(string Path, char IndexStatus, char WorktreeStatus, string? OldPath, string? ContentHash);
