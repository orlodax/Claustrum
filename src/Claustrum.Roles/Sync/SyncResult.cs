namespace Claustrum.Roles.Sync;

/// <summary>
/// Outcome of a `claustrum sync` run: <paramref name="Foreign"/> files exist without a claustrum marker and were
/// left untouched (need `--force` to adopt); <paramref name="Skipped"/> already matched what would be generated;
/// <paramref name="Written"/> is what changed on disk in <see cref="SyncMode.Write"/> or would have in
/// <see cref="SyncMode.DryRun"/>/<see cref="SyncMode.Check"/>. <paramref name="ProposedContent"/> is populated
/// only outside <see cref="SyncMode.Write"/>: each <paramref name="Written"/> path's would-be content, for the CLI
/// to diff against what is on disk. <paramref name="Symlinks"/> maps each symlinked path met (foreign, or in
/// <paramref name="Written"/> because <c>--force</c> replaces the link) to its link target.
/// <paramref name="Refused"/> maps each target resolving outside the repo root to its real path: untouched,
/// <c>--force</c> does not override (#95). <paramref name="RealPaths"/> maps every target whose real path differs
/// from the path as typed; for an agent file that is the landing path (its directory resolved plus its own name),
/// so a replaced leaf link maps to where the new file goes, not to the link's target.
/// <paramref name="IgnoredOverrides"/> lists the target roles whose repo-local override <c>--global</c> ignored (#99).
/// <paramref name="Decisions"/> is what `--dry-run` prints per agent file: the model chosen and why (#53).
/// </summary>
public sealed record SyncResult(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Foreign,
    IReadOnlyDictionary<string, string>? ProposedContent = null,
    IReadOnlyDictionary<string, string>? Symlinks = null,
    IReadOnlyDictionary<string, string>? Refused = null,
    IReadOnlyDictionary<string, string>? RealPaths = null,
    IReadOnlyList<string>? IgnoredOverrides = null,
    IReadOnlyList<string>? Decisions = null);
