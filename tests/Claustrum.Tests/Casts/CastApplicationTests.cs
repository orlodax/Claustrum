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
        (string tier, ConfigOverrides overrides, CastBudget? budget) = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", tier);
        Assert.Null(overrides.Model);
        Assert.Null(budget);
    }

    [Fact]
    public void ADefaultCastFileAppliesItselfWithoutBeingNamed()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: "xhigh") },
            BudgetUsd: 7m));

        (string tier, ConfigOverrides overrides, CastBudget? budget) = CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("xhigh", tier);
        Assert.Equal("claude:opus", overrides.Model);
        Assert.Equal(7m, budget!.Value.Value);
    }

    [Fact]
    public void ANamedCastIsLoadedInsteadOfDefault()
    {
        CastStore.Save(cwd, new Cast("staging", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "claude:haiku", Backend: null, Tier: null) }, null));

        (_, ConfigOverrides overrides, _) = CastApplication.Resolve(cwd, "builder", castName: "staging", tierFlag: null, new ConfigOverrides());

        Assert.Equal("claude:haiku", overrides.Model);
    }

    [Fact]
    public void ARoleNotListedInTheCastGetsNoOverrideContribution()
    {
        CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect("host"), [], null));

        (string tier, ConfigOverrides overrides, CastBudget? budget) = CastApplication.Resolve(cwd, "tester", castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("high", tier);
        Assert.Null(overrides.Model);
        Assert.NotNull(budget);
        Assert.Null(budget!.Value.Value);
    }
}
