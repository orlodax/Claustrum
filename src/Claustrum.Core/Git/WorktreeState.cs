namespace Claustrum.Core.Git;

// Exactly one of StatusEntries (git repo) or FileScan (non-git cwd, mtime/size fallback per
// docs/PLAN.md A2) is meaningful, selected by IsGit.
public sealed record WorktreeState(bool IsGit, GitStatusEntry[] StatusEntries, Dictionary<string, (long Size, DateTime ModifiedUtc)>? FileScan);
