namespace Claustrum.Core.Model;

// The Roles → Core seam (docs/PLAN.md B2): produced by RoleRenderer.Render, still unresolved —
// ModelClass/Permission are string forms from role.json or config, not yet looked up. Deny is the
// role's own baked-in list; Config.Resolve concatenates config-layer deny on top of it.
public sealed record RenderedRole(
    string Name,
    string SystemBody,
    string ModelClass,
    string Effort,
    string Permission,
    string[] Deny,
    string ReportSchema,
    bool Blind);
