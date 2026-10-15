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
    public static CastApplicationResult Resolve(
        string cwd, string role, string? castName, string? tierFlag, ConfigOverrides overrides)
    {
        Cast? cast = castName is { Length: > 0 } ? CastStore.Load(cwd, castName) : CastStore.TryLoadDefault(cwd);

        // The architect's model/tier live in cast.Architect, not in cast.Roles (Cast.ArchitectRole),
        // so `claustrum run architect --cast x` and `coordinate` resolve through this one path.
        CastRoleEntry? castRole = role == Cast.ArchitectRole
            ? cast is null ? null : new CastRoleEntry(cast.Architect.Model, Backend: null, cast.Architect.Tier, Resolved: cast.Architect.Resolved)
            : cast?.Roles.GetValueOrDefault(role);
        (string tier, ConfigOverrides merged) = CastResolution.ApplyRole(tierFlag, overrides, castRole);

        string[] warnings = DescribeWarnings(cwd, cast, role, castRole, overrides);

        return new CastApplicationResult
        {
            Tier = tier,
            Overrides = merged,
            CastBudget = cast is null ? null : new CastBudget(cast.BudgetUsd),
            MaxParallel = castRole?.MaxParallel,
            CastName = cast?.Name,
            Warnings = warnings,
        };
    }

    // #121: never silently run a model the cast's alias only resolved to by falling through to a
    // built-in default, nor one whose stored `resolved` no longer matches today's config. Runs only
    // when the model came from the cast (no --model or --backend flag filled it first): an explicit
    // flag wins and its run gets no cast-caused warning.
    // Config loaded here is a second load (one per run, one per DescribeWarnings call), which is
    // fine — the alternative (reading the config before DelegateEngine.Prepare loads its own) would
    // pull the loaded instance out of a shared slot and complicate thread safety for negligible gain.
    private static string[] DescribeWarnings(string cwd, Cast? cast, string role, CastRoleEntry? castRole, ConfigOverrides overrides)
    {
        if (cast is null || overrides.Model is not null || overrides.Backend is not null || castRole?.Model is not { } alias)
            return [];

        Config config = Config.Load(AppServices.Platform, cwd);
        ModelResolution resolution = config.Describe(alias);
        string target = $"{resolution.Backend}:{resolution.ModelId}";

        List<string> warnings = [];
        if (resolution.FromBuiltin)
            warnings.Add(CastModelLines.BuiltinDefaultWarning(cast.Name, role, resolution, config));
        if (castRole.Resolved is { } stored && stored != target)
            warnings.Add(CastModelLines.ResolvedChangedWarning(cast.Name, role, alias, stored, target, resolution.Source ?? ""));

        return [.. warnings];
    }
}
