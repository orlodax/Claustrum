using Claustrum.Core.Model;

namespace Claustrum.Core.Git;

public sealed record SnapshotDiff(ChangedFile[] ChangedFiles, string? Diff, bool Truncated);
