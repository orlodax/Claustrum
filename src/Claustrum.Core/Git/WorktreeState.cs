namespace Claustrum.Core.Git;

// Exactly one shape is meaningful per capture (docs/PLAN.md A2 non-git fallback): a git repo yields
// per-path status entries, anything else falls back to an mtime/size scan. Modelled as two record
// types under one abstract base instead of a bool flag plus a nullable pair, so WorktreeSnapshot's
// consumer pattern-matches on the real shape instead of null-forgiving into whichever side applies.
public abstract record WorktreeState
{
    public sealed record Git(GitStatusEntry[] StatusEntries) : WorktreeState;

    public sealed record FileScan(Dictionary<string, (long Size, DateTime ModifiedUtc)> Files) : WorktreeState;
}
