namespace Claustrum.Roles.Sync;

/// <summary>
/// Outcome of a `claustrum sync` run: <paramref name="Foreign"/> files exist without a claustrum
/// marker and were left untouched (need `--force` to adopt); <paramref name="Skipped"/> already
/// matched what would be generated.
/// </summary>
public sealed record SyncResult(IReadOnlyList<string> Written, IReadOnlyList<string> Skipped, IReadOnlyList<string> Foreign);
