namespace Claustrum.Core.Git;

// One entry from `git status --porcelain=v1 -z` (docs/PLAN.md A2). IndexStatus/WorktreeStatus are
// the raw XY letters; OldPath is set only for R/C (rename/copy) entries.
public sealed record GitStatusEntry(string Path, char IndexStatus, char WorktreeStatus, string? OldPath);
