using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// The five parallel lists every Sync used to thread through every private method as out-parameters
/// — ClaudeSync's own doc comment called that "already a smell", and copying the signature into
/// OpencodeSync and CopilotSync made it a three-way one (review finding). One object instead.
/// </summary>
public sealed class SyncAccumulator
{
    public List<string> Written { get; } = [];

    public List<string> Skipped { get; } = [];

    public List<string> Foreign { get; } = [];

    public List<SyncManifestFile> ManifestFiles { get; } = [];

    public Dictionary<string, string> ProposedContent { get; } = [];

    /// <summary>Path → link target for every symlink met at a path sync would write (issue #84).</summary>
    public Dictionary<string, string> Symlinks { get; } = [];

    /// <summary>Path → real path for every target refused because it resolves outside the repo root (issue #95).</summary>
    public Dictionary<string, string> Refused { get; } = [];

    /// <summary>Path → real path for every target met whose real path differs from the path as typed, refused or not.</summary>
    public Dictionary<string, string> RealPaths { get; } = [];

    /// <summary>Target roles whose repo-local override `--global` ignored (issue #99); empty in repo mode.</summary>
    public List<string> IgnoredOverrides { get; } = [];

    /// <summary>One line per agent file saying which model `claustrum.json` or the defaults chose, and what could not be rendered (#53).</summary>
    public List<string> Decisions { get; } = [];

    /// <summary>ProposedContent is only meaningful outside <see cref="SyncMode.Write"/>.</summary>
    public SyncResult ToResult(SyncMode mode) =>
        new(Written, Skipped, Foreign, mode == SyncMode.Write ? null : ProposedContent, Symlinks, Refused, RealPaths, IgnoredOverrides, Decisions);
}
