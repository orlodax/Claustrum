using Claustrum.Core.Config;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Roles.Tests.Sync;

// #53: which model a harness's agent file names for a tier, and the config-layer effort and deny `run` applies. A null config
// is the library's own defaults; an alias that lands on the harness is used; one that lands elsewhere cannot be written into
// that harness's file and falls back, saying why. ValidateModels turns an alias cycle into an error before any file is written.
public sealed class SyncSettingsTests
{
    private static readonly RoleTier highTier = new("frontier-coding", "high");
    private static readonly RoleTier maxTier = new("frontier-coding", "max");

    private static string Default(string modelClass) => $"default-for-{modelClass}";

    [Fact]
    public void ANullConfigGivesTheLibrarysDefaults()
    {
        TierChoice choice = new SyncSettings(null).Choose("claude", "builder", "high", highTier, Default);

        Assert.Equal("default-for-frontier-coding", choice.Model);
        Assert.Equal("high", choice.Effort);
        Assert.Empty(choice.Deny);
        Assert.Contains("default for class 'frontier-coding'", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasLandingOnTheHarnessIsUsed()
    {
        SyncSettings settings = new(SyncConfigs.Make(models: new() { ["frontier-coding"] = "claude:opus-9" }));

        TierChoice choice = settings.Choose("claude", "builder", "high", highTier, Default);

        Assert.Equal("opus-9", choice.Model);
        Assert.Contains("alias 'frontier-coding'", choice.Reason, StringComparison.Ordinal);
        Assert.Contains("claude:opus-9", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasOnAnotherBackendFallsBackToTheDefaultAndSaysWhy()
    {
        SyncSettings settings = new(SyncConfigs.Make(models: new() { ["frontier-coding"] = "opencode:some/model" }));

        TierChoice choice = settings.Choose("claude", "builder", "high", highTier, Default);

        Assert.Equal("default-for-frontier-coding", choice.Model);
        Assert.Contains("cannot be used in claude agent files", choice.Reason, StringComparison.Ordinal);
        Assert.Contains("opencode:some/model", choice.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void ARolesModelAppliesToEveryTier(string tierName)
    {
        SyncSettings settings = new(SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings("claude:pinned", null, null, null) }));

        TierChoice choice = settings.Choose("claude", "builder", tierName, new RoleTier("frontier-coding", tierName), Default);

        Assert.Equal("pinned", choice.Model);
        Assert.Contains("roles.builder.model", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARolesEffortChangesOnlyTheBaseAgent()
    {
        SyncSettings settings = new(SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings(null, "low", null, null) }));

        Assert.Equal("low", settings.Choose("claude", "builder", "high", highTier, Default).Effort);
        Assert.Equal("max", settings.Choose("claude", "builder", "max", maxTier, Default).Effort);
    }

    [Fact]
    public void ARolesDenyRidesOnTheChoice()
    {
        SyncSettings settings = new(SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings(null, null, null, ["git commit"]) }));

        Assert.Equal(["git commit"], settings.Choose("claude", "builder", "high", highTier, Default).Deny);
    }

    // A typo in a local role.json must not be written into an agent file as a literal model id.
    [Fact]
    public void ATierClassThatIsNoAliasAndNoBackendIdFallsBack()
    {
        SyncSettings settings = new(SyncConfigs.Make());

        TierChoice choice = settings.Choose("claude", "builder", "high", new RoleTier("frontier-codng", "high"), Default);

        Assert.Equal("default-for-frontier-codng", choice.Model);
        Assert.Contains("(no alias)", choice.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATierClassThatIsABackendIdIsUsedWithoutAnAlias()
    {
        SyncSettings settings = new(SyncConfigs.Make());

        Assert.Equal("direct", settings.Choose("claude", "builder", "high", new RoleTier("claude:direct", "high"), Default).Model);
    }

    private static readonly RoleLibrary library = new();

    [Fact]
    public void AnAliasCycleInAClassARoleUsesIsAConfigErrorBeforeAnyWrite()
    {
        CoreConfig config = SyncConfigs.Make(models: new() { ["frontier-coding"] = "a", ["a"] = "frontier-coding" });

        Assert.Throws<ConfigException>(() => SyncSettings.ValidateModels(config, library, ["builder"], overrideCwd: null));
        Assert.Throws<ConfigException>(() => SyncSettings.ValidateModels(config, library, null, overrideCwd: null));
    }

    [Fact]
    public void ACycleInAnAliasNoTargetRoleUsesIsNotAnError()
    {
        // builder never names frontier-reasoning (the architect does).
        CoreConfig config = SyncConfigs.Make(models: new() { ["frontier-reasoning"] = "a", ["a"] = "frontier-reasoning" });

        SyncSettings.ValidateModels(config, library, ["builder"], overrideCwd: null);
        Assert.Throws<ConfigException>(() => SyncSettings.ValidateModels(config, library, null, overrideCwd: null));
    }

    [Fact]
    public void AnUnknownClassIsNotAnErrorHere()
    {
        CoreConfig config = SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings("no-such-class", null, null, null) });

        SyncSettings.ValidateModels(config, library, ["builder"], overrideCwd: null);
    }

    [Fact]
    public void ADepthOfThreeResolvesAndFourThrows()
    {
        CoreConfig three = SyncConfigs.Make(models: new() { ["frontier-coding"] = "a", ["a"] = "b", ["b"] = "claude:end" });
        CoreConfig four = SyncConfigs.Make(models: new() { ["frontier-coding"] = "a", ["a"] = "b", ["b"] = "c", ["c"] = "claude:end" });

        SyncSettings.ValidateModels(three, library, ["builder"], overrideCwd: null);
        ConfigException ex = Assert.Throws<ConfigException>(() => SyncSettings.ValidateModels(four, library, ["builder"], overrideCwd: null));
        Assert.Contains("3 levels", ex.Message, StringComparison.Ordinal);
    }
}
