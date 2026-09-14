using Claustrum.Core.Config;

namespace Claustrum.Casts;

// docs/PLAN.md §D1: "Any value may be an alias from claustrum.json.models... explicit --model/
// --backend flags still override per call." Applied once, at the call site (RunCommand/the MCP
// delegate tool), before Config.Resolve ever sees the result — a cast's role entry only fills gaps
// the caller's own flags left open. Pure and AppServices-free by design, so it is unit-testable
// without a real backend/config file.
public static class CastResolution
{
    public static (string Tier, ConfigOverrides Overrides) ApplyRole(string? tierFlag, ConfigOverrides overrides, CastRoleEntry? castRole)
    {
        string tier = tierFlag ?? castRole?.Tier ?? "high";
        ConfigOverrides merged = overrides with
        {
            Backend = overrides.Backend ?? castRole?.Backend,
            Model = overrides.Model ?? castRole?.Model,
        };

        return (tier, merged);
    }

    // flagBudget wins outright; otherwise an active cast's own value (even null, meaning unlimited)
    // is authoritative and skips the config-file default entirely; only with no cast active does the
    // config default apply — the precedence CastBudget's doc comment explains.
    public static decimal? ApplyBudget(decimal? flagBudget, CastBudget? castBudget, decimal? configDefaultBudget) =>
        flagBudget is { } budget ? budget
        : castBudget is { } cast ? cast.Value
        : configDefaultBudget;
}
