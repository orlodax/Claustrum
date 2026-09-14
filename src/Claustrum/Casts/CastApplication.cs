using Claustrum.Core.Config;

namespace Claustrum.Casts;

// Shared by CLI `run` and MCP `delegate`/`delegate_async` (docs/PLAN.md §D1/§D5 "--cast accepted on
// run/delegate"): loads the named cast (or a repo's default.json when none is named) and folds its
// role entry into the tier/overrides a caller already built from its own flags/parameters.
public static class CastApplication
{
    public static (string Tier, ConfigOverrides Overrides, CastBudget? CastBudget) Resolve(
        string cwd, string role, string? castName, string? tierFlag, ConfigOverrides overrides)
    {
        Cast? cast = castName is { Length: > 0 } ? CastStore.Load(cwd, castName) : CastStore.TryLoadDefault(cwd);
        CastRoleEntry? castRole = cast?.Roles.GetValueOrDefault(role);
        (string tier, ConfigOverrides resolvedOverrides) = CastResolution.ApplyRole(tierFlag, overrides, castRole);

        return (tier, resolvedOverrides, cast is null ? null : new CastBudget(cast.BudgetUsd));
    }
}
