namespace Claustrum.Core.Model;

// The Roles → Core seam (docs/PLAN.md B2): produced by RoleRenderer.Render, still unresolved —
// ModelClass/Permission are string forms from role.json or config, not yet looked up. Deny is the
// role's own baked-in list; Config.Resolve concatenates config-layer deny on top of it. Warnings are
// what the render itself could not do as asked (#55); they end up first in the run receipt's `warnings`.
// UserMcp is the role library's default for keeping the operator's user-level MCP servers (#127); config can still say otherwise.
// McpServers are the servers the library's own role.json names in `tools` (#140), never a repo override's.
// PermissionByBackend is role.json's per-backend replacement for Permission (the architect's `codex: full`), below config and flags.
public sealed record RenderedRole(
    string Name,
    string SystemBody,
    string ModelClass,
    string Effort,
    string Permission,
    string[] Deny,
    string ReportSchema,
    bool Blind,
    string[]? Warnings = null,
    bool UserMcp = false,
    string[]? McpServers = null,
    Dictionary<string, string>? PermissionByBackend = null);
