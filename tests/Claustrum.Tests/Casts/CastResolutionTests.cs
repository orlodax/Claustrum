using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Tests.Casts;

public sealed class CastResolutionTests
{
    [Fact]
    public void NoCastRoleAndNoFlagsDefaultsTierToHigh()
    {
        (string tier, ConfigOverrides overrides) = CastResolution.ApplyRole(tierFlag: null, new ConfigOverrides(), castRole: null);

        Assert.Equal("high", tier);
        Assert.Null(overrides.Model);
        Assert.Null(overrides.Backend);
    }

    [Fact]
    public void CastRoleFillsTierModelAndBackendWhenNoFlagGiven()
    {
        CastRoleEntry castRole = new(Model: "claude:opus", Backend: "claude", Tier: "xhigh");

        (string tier, ConfigOverrides overrides) = CastResolution.ApplyRole(tierFlag: null, new ConfigOverrides(), castRole);

        Assert.Equal("xhigh", tier);
        Assert.Equal("claude:opus", overrides.Model);
        Assert.Equal("claude", overrides.Backend);
    }

    [Fact]
    public void ExplicitFlagsWinOverTheCastRoleEntry()
    {
        CastRoleEntry castRole = new(Model: "claude:opus", Backend: "claude", Tier: "xhigh");
        ConfigOverrides flags = new(Model: "claude:haiku", Backend: "api");

        (string tier, ConfigOverrides overrides) = CastResolution.ApplyRole(tierFlag: "max", flags, castRole);

        Assert.Equal("max", tier);
        Assert.Equal("claude:haiku", overrides.Model);
        Assert.Equal("api", overrides.Backend);
    }

    [Fact]
    public void NoCastActiveBudgetFallsBackToConfigDefault()
    {
        decimal? budget = CastResolution.ApplyBudget(flagBudget: null, castBudget: null, configDefaultBudget: 5m);

        Assert.Equal(5m, budget);
    }

    [Fact]
    public void ActiveCastWithNullBudgetMeansUnlimitedAndSkipsTheConfigDefault()
    {
        decimal? budget = CastResolution.ApplyBudget(flagBudget: null, castBudget: new CastBudget(null), configDefaultBudget: 5m);

        Assert.Null(budget);
    }

    [Fact]
    public void ActiveCastWithAValueWinsOverTheConfigDefault()
    {
        decimal? budget = CastResolution.ApplyBudget(flagBudget: null, castBudget: new CastBudget(10m), configDefaultBudget: 5m);

        Assert.Equal(10m, budget);
    }

    [Fact]
    public void ExplicitFlagBudgetWinsOverAnActiveCast()
    {
        decimal? budget = CastResolution.ApplyBudget(flagBudget: 2m, castBudget: new CastBudget(null), configDefaultBudget: 5m);

        Assert.Equal(2m, budget);
    }
}
