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

    /// <summary>ProposedContent is only meaningful outside <see cref="SyncMode.Write"/>.</summary>
    public SyncResult ToResult(SyncMode mode) =>
        new(Written, Skipped, Foreign, mode == SyncMode.Write ? null : ProposedContent);
}
