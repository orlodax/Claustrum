using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
// Alias, not a bare `Config`: see ConfigTests for the namespace-lookup trap this sidesteps.
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #121: Config.Describe answers "which alias runs which model, and why" - the same chase ResolveModel does, plus the layer
// of the first hop and whether any hop fell through to a built-in default. ResolveModel's own rules (depth 3, cycles, the
// too-deep refusal) are the contract Describe rides on and did not change.
public sealed class ConfigDescribeTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-describe-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-describe-home"));
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static CoreConfig Load(string? userJson = null, string? repoJson = null)
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };
        if (userJson is not null)
            platform.Files[userConfigPath] = userJson;

        if (repoJson is not null)
        {
            platform.Files[repoConfigPath] = repoJson;
            platform.ExtraExistingPaths.Add(Path.Combine(repoDir, ".git"));
        }

        return CoreConfig.Load(platform, repoDir);
    }

    [Fact]
    public void ABuiltInAliasNamesTheBuiltInLayerAndFlagsTheFallthrough()
    {
        ModelResolution resolution = Load().Describe("cheap-coding");

        Assert.Equal(new ModelResolution("cheap-coding", "claude", "haiku", ConfigLayer.Builtin, "built-in default", FromBuiltin: true, BuiltinHop: "cheap-coding"), resolution);
    }

    [Fact]
    public void AUserAliasNamesTheUserConfigPathAndIsNotAFallthrough()
    {
        ModelResolution resolution = Load(userJson: /*lang=json,strict*/ """{"models":{"cheap-coding":"opencode:vendor/model"}}""").Describe("cheap-coding");

        Assert.Equal(new ModelResolution("cheap-coding", "opencode", "vendor/model", ConfigLayer.User, userConfigPath, FromBuiltin: false, BuiltinHop: null), resolution);
    }

    [Fact]
    public void ARepoAliasNamesTheRepoFileAndWinsOverTheUserOne()
    {
        ModelResolution resolution = Load(
            userJson: /*lang=json,strict*/ """{"models":{"fast":"claude:from-user"}}""",
            repoJson: /*lang=json,strict*/ """{"models":{"fast":"claude:from-repo"}}""").Describe("fast");

        Assert.Equal(ConfigLayer.Repo, resolution.Layer);
        Assert.Equal(repoConfigPath, resolution.Source);
        Assert.Equal("from-repo", resolution.ModelId);
        Assert.False(resolution.FromBuiltin);
    }

    [Theory]
    [InlineData("opencode:openrouter/vendor/model", "opencode", "openrouter/vendor/model")]
    [InlineData("claude:opus", "claude", "opus")]
    [InlineData("sonnet", "claude", "sonnet")]
    public void AFreeFormSpecIsNoAliasSoItHasNoLayerNoSourceAndNoFallthrough(string spec, string backend, string modelId)
    {
        ModelResolution resolution = Load().Describe(spec);

        Assert.Equal(new ModelResolution(spec, backend, modelId, Layer: null, Source: null, FromBuiltin: false, BuiltinHop: null), resolution);
    }

    // The silent case #121 exists for: the user config defines the alias, so "no config defines it" would be false, but the
    // model it lands on is still a built-in default.
    [Fact]
    public void AUserAliasChainedToABuiltInNamesTheBuiltInHop()
    {
        ModelResolution resolution = Load(userJson: /*lang=json,strict*/ """{"models":{"mine":"cheap-coding"}}""").Describe("mine");

        Assert.Equal(ConfigLayer.User, resolution.Layer);
        Assert.Equal(userConfigPath, resolution.Source);
        Assert.True(resolution.FromBuiltin);
        Assert.Equal("cheap-coding", resolution.BuiltinHop);
        Assert.Equal(("claude", "haiku"), (resolution.Backend, resolution.ModelId));
    }

    [Fact]
    public void ABuiltInHopLaterInTheChainIsStillTheFirstBuiltInHop()
    {
        ModelResolution resolution = Load(userJson: /*lang=json,strict*/ """{"models":{"a":"b","b":"fast"}}""").Describe("a");

        Assert.Equal("fast", resolution.BuiltinHop);
        Assert.Equal(ConfigLayer.User, resolution.Layer);
    }

    // ---- ResolveModel: depth 3, cycles and the too-deep refusal are unchanged by #121 ----------------------------

    [Fact]
    public void ThreeHopsResolveAndTheTerminalValueIsSplitIntoBackendAndModel()
    {
        CoreConfig config = Load(userJson: /*lang=json,strict*/ """{"models":{"a":"b","b":"c","c":"claude:opus"}}""");

        Assert.Equal(("claude", "opus"), config.ResolveModel("a"));
        Assert.Equal(("claude", "opus"), (config.Describe("a").Backend, config.Describe("a").ModelId));
    }

    [Fact]
    public void FourHopsAreRefusedAsNotResolvedWithinThreeLevels()
    {
        CoreConfig config = Load(userJson: /*lang=json,strict*/ """{"models":{"a":"b","b":"c","c":"d","d":"claude:opus"}}""");

        ConfigException error = Assert.Throws<ConfigException>(() => config.ResolveModel("a"));

        Assert.Equal("model alias 'a' did not resolve within 3 levels", error.Message);
        Assert.Equal(error.Message, Assert.Throws<ConfigException>(() => config.Describe("a")).Message);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"models":{"a":"b","b":"a"}}""", "a", "model alias 'a' has a cycle at 'a'")]
    [InlineData(/*lang=json,strict*/ """{"models":{"a":"a"}}""", "a", "model alias 'a' has a cycle at 'a'")]
    [InlineData(/*lang=json,strict*/ """{"models":{"a":"b","b":"c","c":"b"}}""", "a", "model alias 'a' has a cycle at 'b'")]
    public void ACycleIsRefusedNamingTheAliasItReturnsTo(string userJson, string spec, string expected)
    {
        CoreConfig config = Load(userJson: userJson);

        Assert.Equal(expected, Assert.Throws<ConfigException>(() => config.ResolveModel(spec)).Message);
        Assert.Equal(expected, Assert.Throws<ConfigException>(() => config.Describe(spec)).Message);
    }
}
