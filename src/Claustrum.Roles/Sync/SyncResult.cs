namespace Claustrum.Roles.Sync;

/// <summary>
/// Outcome of a `claustrum sync` run: <paramref name="Foreign"/> files exist without a claustrum
/// marker and were left untouched (need `--force` to adopt); <paramref name="Skipped"/> already
/// matched what would be generated; <paramref name="Written"/> is what changed on disk in
/// <see cref="SyncMode.Write"/> or would have in <see cref="SyncMode.DryRun"/>/<see cref="SyncMode.Check"/>.
/// <paramref name="ProposedContent"/> is populated only outside <see cref="SyncMode.Write"/>: each
/// <paramref name="Written"/> path's would-be content, for the CLI to diff against what is on disk.
/// </summary>
public sealed record SyncResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Foreign,
    IReadOnlyDictionary<string, string>? ProposedContent = null);
