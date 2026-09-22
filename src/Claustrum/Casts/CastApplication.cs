using Claustrum.Core.Config;

namespace Claustrum.Casts;

// Shared by CLI `run` and MCP `delegate`/`delegate_async` (docs/PLAN.md §D1/§D5 "--cast accepted on
// run/delegate"): loads the named cast (or a repo's default.json when none is named) and folds its
// role entry into the tier/overrides a caller already built from its own flags/parameters.
public static class CastApplication
{
    // CastName is the cast that actually applied (the --cast argument, or "default" when
    // default.json applied itself), null when no cast was in play at all. RoleConcurrencyGate keys
    // its slot pool on it: docs/PLAN.md §D4 caps per cast, and a pool keyed on the role alone is
    // shared by every cast in the repo.
    public static (string Tier, ConfigOverrides Overrides, CastBudget? CastBudget, int? MaxParallel, string? CastName) Resolve(
        string cwd, string role, string? castName, string? tierFlag, ConfigOverrides overrides)
    {
        Cast? cast = castName is { Length: > 0 } ? CastStore.Load(cwd, castName) : CastStore.TryLoadDefault(cwd);

        // The architect's model/tier live in cast.Architect, not in cast.Roles (Cast.ArchitectRole),
        // so `claustrum run architect --cast x` and `coordinate` resolve through this one path.
        CastRoleEntry? castRole = role == Cast.ArchitectRole
            ? cast is null ? null : new CastRoleEntry(cast.Architect.Model, Backend: null, cast.Architect.Tier)
            : cast?.Roles.GetValueOrDefault(role);
        (string tier, ConfigOverrides resolvedOverrides) = CastResolution.ApplyRole(tierFlag, overrides, castRole);

        return (tier, resolvedOverrides, cast is null ? null : new CastBudget(cast.BudgetUsd), castRole?.MaxParallel, cast?.Name);
    }
}
