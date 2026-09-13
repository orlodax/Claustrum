namespace Claustrum.Core.Model;

// Backend/Model/Permission are fully resolved (docs/PLAN.md A2). Blind and HasReport survive from
// RenderedRole because Runner.RunAsync only receives this record: the blind gate
// (NOTES.md "Blind review is enforced, not requested") must see Blind before touching a backend, and
// HasReport tells Runner whether to append the report-format reminder trailer to the user prompt
// (tester report: the model dropped the report fence on ~half of trivial one-line tasks when the
// instruction only lived at the end of a long system prompt).
public sealed record ResolvedRole(
    string Name,
    string SystemPrompt,
    string Backend,
    string Model,
    string Effort,
    PermissionPolicy Permission,
    bool Blind,
    bool HasReport);
