namespace Claustrum.Core.Model;

// Backend/Model/Permission are fully resolved (docs/PLAN.md A2). Blind survives from
// RenderedRole because Runner.RunAsync only receives this record, and the blind gate
// (NOTES.md "Blind review is enforced, not requested") must see it before touching a backend.
public sealed record ResolvedRole(
    string Name,
    string SystemPrompt,
    string Backend,
    string Model,
    string Effort,
    PermissionPolicy Permission,
    bool Blind);
