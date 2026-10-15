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
        CastApplicationResult applied = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", applied.Tier);
        Assert.Null(applied.Overrides.Model);
        Assert.Null(applied.CastBudget);
        Assert.Null(applied.MaxParallel);
        Assert.Null(applied.CastName);
        Assert.Empty(applied.Warnings);
    }

    [Fact]
    public void ADefaultCastFileAppliesItselfWithoutBeingNamed()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: "xhigh") },
            BudgetUsd: 7m));

        CastApplicationResult applied = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("xhigh", applied.Tier);
        Assert.Equal("claude:opus", applied.Overrides.Model);
        Assert.Equal(7m, applied.CastBudget!.Value.Value);
        Assert.Null(applied.MaxParallel);
    }

    [Fact]
    public void ANamedCastIsLoadedInsteadOfDefault()
    {
        CastStore.Save(cwd, new Cast("staging", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:haiku", Backend: null, Tier: null) }, null));

        CastApplicationResult applied = CastApplication.Resolve(cwd, "builder", castName: "staging", tierFlag: null, new ConfigOverrides());

        Assert.Equal("claude:haiku", applied.Overrides.Model);
    }

    [Fact]
    public void ACastRolesMaxParallelIsSurfacedForTheCaller()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null, MaxParallel: 3) },
            BudgetUsd: null));

        CastApplicationResult applied = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal(3, applied.MaxParallel);

        // RoleConcurrencyGate keys its slot pool on this: per cast, not per role (docs/PLAN.md §D4).
        Assert.Equal("default", applied.CastName);
    }

    [Fact]
    public void ARoleNotListedInTheCastGetsNoOverrideContribution()
    {
        CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect("host"), [], null));

        CastApplicationResult applied = CastApplication.Resolve(cwd, "tester", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", applied.Tier);
        Assert.Null(applied.Overrides.Model);
        Assert.NotNull(applied.CastBudget);
        Assert.Null(applied.CastBudget!.Value.Value);
        Assert.Null(applied.MaxParallel);
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

        CastApplicationResult applied = CastApplication.Resolve(cwd, Cast.ArchitectRole, castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("xhigh", applied.Tier);
        Assert.Equal("claude:opus", applied.Overrides.Model);
    }

    // docs/PLAN.md §D1's precedence: an explicit --model/--tier still wins over the cast's own value,
    // for the architect exactly as for any other role.
    [Fact]
    public void ExplicitModelAndTierFlagsStillWinOverTheCastsArchitectEntry()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus", Tier: "xhigh"),
            [], null));

        CastApplicationResult applied = CastApplication.Resolve(
            cwd, Cast.ArchitectRole, castName: null, tierFlag: "max", new ConfigOverrides(Model: "claude:haiku"));

        Assert.Equal("max", applied.Tier);
        Assert.Equal("claude:haiku", applied.Overrides.Model);
    }
}
