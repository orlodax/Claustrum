namespace Claustrum.Casts;

// Call-site orchestration data, not a Core concept: like RunOptions/BackendConfig
// (Claustrum.Core/RunOptions.cs "call-site concerns, not request data"), Core's Runner and Config
// never see a Cast — DelegateEngine folds a cast's role entry into ConfigOverrides and the render
// tier before calling Config.Resolve (docs/PLAN.md §D1). `Roles` maps a role name to its entry;
// a present key with a null value means "not needed for this cast" (e.g. `"ui-reviewer": null`).
public sealed record Cast(
    string Name,
    string Library,
    CastArchitect Architect,
    Dictionary<string, CastRoleEntry?> Roles,
    decimal? BudgetUsd)
{
    /// <summary>
    /// The one role name that is never a key of <see cref="Roles"/>: the architect's model and tier
    /// live in <see cref="Architect"/> (docs/PLAN.md §D3), which is where CastQuestionnaire,
    /// CastBuilder and CastApplication all route it.
    /// </summary>
    public const string ArchitectRole = "architect";
}
