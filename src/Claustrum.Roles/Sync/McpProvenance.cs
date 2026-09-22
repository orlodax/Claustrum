namespace Claustrum.Roles.Sync;

/// <summary>
/// What decides whether an existing <c>claustrum</c> key in a JSON config may be rewritten.
/// <see cref="Manifest"/> is the in-repo rule: a key with no matching `.claustrum/sync-manifest.json`
/// entry is a human's. <see cref="OwnKey"/> is for the targets that live outside any repo (`--global`:
/// the Claude desktop app's config, `~/.cursor/mcp.json`), where no manifest exists to consult — there
/// the key's own name is the provenance, so claustrum rewrites it whenever it differs. Sibling servers
/// are never touched under either rule.
/// </summary>
internal enum McpProvenance
{
    Manifest,
    OwnKey,
}
