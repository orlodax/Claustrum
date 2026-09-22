using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Tests.Casts;

public sealed class CastApplicationTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-application-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public void NoCastFileAndNoCastNameLeavesOverridesAndDefaultsTierToHigh()
    {
        (string tier, ConfigOverrides overrides, CastBudget? budget, int? maxParallel, string? castNameOut) = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", tier);
        Assert.Null(overrides.Model);
        Assert.Null(budget);
        Assert.Null(maxParallel);
        Assert.Null(castNameOut);
    }

    [Fact]
    public void ADefaultCastFileAppliesItselfWithoutBeingNamed()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: "xhigh") },
            BudgetUsd: 7m));

        (string tier, ConfigOverrides overrides, CastBudget? budget, int? maxParallel, _) = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("xhigh", tier);
        Assert.Equal("claude:opus", overrides.Model);
        Assert.Equal(7m, budget!.Value.Value);
        Assert.Null(maxParallel);
    }

    [Fact]
    public void ANamedCastIsLoadedInsteadOfDefault()
    {
        CastStore.Save(cwd, new Cast("staging", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:haiku", Backend: null, Tier: null) }, null));

        (_, ConfigOverrides overrides, _, _, _) = CastApplication.Resolve(cwd, "builder", castName: "staging", tierFlag: null, new ConfigOverrides());

        Assert.Equal("claude:haiku", overrides.Model);
    }

    [Fact]
    public void ACastRolesMaxParallelIsSurfacedForTheCaller()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null, MaxParallel: 3) },
            BudgetUsd: null));

        (_, _, _, int? maxParallel, string? castNameOut) = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal(3, maxParallel);

        // RoleConcurrencyGate keys its slot pool on this: per cast, not per role (docs/PLAN.md §D4).
        Assert.Equal("default", castNameOut);
    }

    [Fact]
    public void ARoleNotListedInTheCastGetsNoOverrideContribution()
    {
        CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect("host"), [], null));

        (string tier, ConfigOverrides overrides, CastBudget? budget, int? maxParallel, _) = CastApplication.Resolve(cwd, "tester", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", tier);
        Assert.Null(overrides.Model);
        Assert.NotNull(budget);
        Assert.Null(budget!.Value.Value);
        Assert.Null(maxParallel);
    }

    // The architect is never a key of Cast.Roles (Cast.ArchitectRole's own doc comment): its
    // model/tier live in Cast.Architect, and CastApplication.Resolve has to route "architect" there
    // instead of looking it up in Roles (where it would always be a miss).
    [Fact]
    public void ArchitectRoleTakesModelAndTierFromCastArchitectNotFromRoles()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus", Tier: "xhigh"),
            [], null));

        (string tier, ConfigOverrides overrides, _, _, _) = CastApplication.Resolve(cwd, Cast.ArchitectRole, castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("xhigh", tier);
        Assert.Equal("claude:opus", overrides.Model);
    }

    // docs/PLAN.md §D1's precedence: an explicit --model/--tier still wins over the cast's own value,
    // for the architect exactly as for any other role.
    [Fact]
    public void ExplicitModelAndTierFlagsStillWinOverTheCastsArchitectEntry()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus", Tier: "xhigh"),
            [], null));

        (string tier, ConfigOverrides overrides, _, _, _) = CastApplication.Resolve(
            cwd, Cast.ArchitectRole, castName: null, tierFlag: "max", new ConfigOverrides(Model: "claude:haiku"));

        Assert.Equal("max", tier);
        Assert.Equal("claude:haiku", overrides.Model);
    }
}
