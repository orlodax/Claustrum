using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
// Alias for the same namespace-lookup trap ConfigTests.cs documents.
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #137: `defaults.budget_usd` is a DefaultBudget? per layer: null = the layer is silent (the earlier layer's cap stands), a DefaultBudget with
// a null Usd = the layer turns the default cap off (`"budget_usd": null` in a file, CLAUSTRUM_BUDGET_USD=none|null in the environment).
public sealed class ConfigBudgetTests
{
    private const string Key = "defaults.budget_usd";

    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));
    private static readonly string gitMarker = Path.Combine(repoDir, ".git");
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static FakePlatform NewPlatform(string? user = null, string? repo = null, string? env = null)
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };
        if (user is not null)
            platform.Files[userConfigPath] = user;

        if (repo is not null)
        {
            platform.Files[repoConfigPath] = repo;
            platform.ExtraExistingPaths.Add(gitMarker);
        }

        if (env is not null)
            platform.EnvironmentVariables["CLAUSTRUM_BUDGET_USD"] = env;

        return platform;
    }

    private static CoreConfig Load(FakePlatform platform) => CoreConfig.Load(platform, repoDir);

    private const string UserNull = /*lang=json,strict*/ """{"defaults":{"budget_usd":null}}""";

    [Fact]
    public void NoKeyAnywhereKeepsTheBuiltInFiveWithBuiltinOrigin()
    {
        CoreConfig config = Load(NewPlatform());

        Assert.Equal(5m, config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Builtin, config.Origins[Key]);
    }

    [Fact]
    public void ANullInTheUserConfigTurnsTheDefaultCapOffWithUserOrigin()
    {
        CoreConfig config = Load(NewPlatform(user: UserNull));

        DefaultBudget? budget = config.Merged.Defaults?.BudgetUsd;
        Assert.NotNull(budget);
        Assert.Null(budget.Value.Usd);
        Assert.Equal(ConfigLayer.User, config.Origins[Key]);
    }

    [Fact]
    public void ANumberInTheRepoConfigBeatsANullInTheUserConfig()
    {
        CoreConfig config = Load(NewPlatform(user: UserNull, repo: /*lang=json,strict*/ """{"defaults":{"budget_usd":7.5}}"""));

        Assert.Equal(7.5m, config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Repo, config.Origins[Key]);
    }

    [Fact]
    public void ANullInTheRepoConfigBeatsANumberInTheUserConfig()
    {
        CoreConfig config = Load(NewPlatform(
            user: /*lang=json,strict*/ """{"defaults":{"budget_usd":3}}""",
            repo: /*lang=json,strict*/ """{"defaults":{"budget_usd":null}}"""));

        DefaultBudget? budget = config.Merged.Defaults?.BudgetUsd;
        Assert.NotNull(budget);
        Assert.Null(budget.Value.Usd);
        Assert.Equal(ConfigLayer.Repo, config.Origins[Key]);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"models":{"fast":"claude:haiku"}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"timeout_seconds":60}}""")]
    public void ARepoConfigThatOmitsTheKeyKeepsTheUsersNullAndItsOrigin(string repoJson)
    {
        CoreConfig config = Load(NewPlatform(user: UserNull, repo: repoJson));

        DefaultBudget? budget = config.Merged.Defaults?.BudgetUsd;
        Assert.NotNull(budget);
        Assert.Null(budget.Value.Usd);
        Assert.Equal(ConfigLayer.User, config.Origins[Key]);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData(" null ")]
    [InlineData("NULL")]
    public void ANoneOrNullWordInTheEnvironmentBeatsAFileNumber(string word)
    {
        CoreConfig config = Load(NewPlatform(repo: /*lang=json,strict*/ """{"defaults":{"budget_usd":9}}""", env: word));

        DefaultBudget? budget = config.Merged.Defaults?.BudgetUsd;
        Assert.NotNull(budget);
        Assert.Null(budget.Value.Usd);
        Assert.Equal(ConfigLayer.Env, config.Origins[Key]);
    }

    [Fact]
    public void ANoneWordInTheEnvironmentTurnsOffTheBuiltInDefaultToo()
    {
        CoreConfig config = Load(NewPlatform(env: "none"));

        Assert.Null(config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Env, config.Origins[Key]);
    }

    [Fact]
    public void ANumberInTheEnvironmentBeatsANullInAFile()
    {
        CoreConfig config = Load(NewPlatform(user: UserNull, env: "2.5"));

        Assert.Equal(2.5m, config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Env, config.Origins[Key]);
    }

    [Theory]
    [InlineData("unlimited")]
    [InlineData("nul")]
    [InlineData("")]
    [InlineData("none please")]
    public void ANonNumericEnvironmentWordOtherThanNoneOrNullIsIgnored(string word)
    {
        CoreConfig config = Load(NewPlatform(repo: /*lang=json,strict*/ """{"defaults":{"budget_usd":9}}""", env: word));

        Assert.Equal(9m, config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Repo, config.Origins[Key]);
    }

    [Fact]
    public void AnIgnoredEnvironmentWordLeavesAUserNullStanding()
    {
        CoreConfig config = Load(NewPlatform(user: UserNull, env: "unlimited"));

        Assert.Null(config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.User, config.Origins[Key]);
    }

    [Fact]
    public void ZeroLoadsAsADollarZeroCapNotAsNoCap()
    {
        CoreConfig config = Load(NewPlatform(user: /*lang=json,strict*/ """{"defaults":{"budget_usd":0}}"""));

        DefaultBudget? budget = config.Merged.Defaults?.BudgetUsd;
        Assert.NotNull(budget);
        Assert.Equal(0m, budget.Value.Usd);
        Assert.Equal(ConfigLayer.User, config.Origins[Key]);
    }

    [Fact]
    public void AZeroInTheEnvironmentIsADollarZeroCapNotNoCap()
    {
        CoreConfig config = Load(NewPlatform(env: "0"));

        Assert.Equal(0m, config.Merged.Defaults?.BudgetUsd?.Usd);
        Assert.Equal(ConfigLayer.Env, config.Origins[Key]);
    }

    [Fact]
    public void TheFlagStillWinsOverANullDefaultAndRecordsFlagOrigin()
    {
        CoreConfig config = Load(NewPlatform(user: UserNull));

        config.Resolve(new RenderedRole("builder", "body", "frontier-coding", "high", "edit+shell", [], "builder", Blind: false), new ConfigOverrides(BudgetUsd: 3m));

        Assert.Equal(ConfigLayer.Flag, config.Origins[Key]);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":"none"}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":"5"}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":true}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":false}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":{"usd":5}}}""")]
    [InlineData(/*lang=json,strict*/ """{"defaults":{"budget_usd":[5]}}""")]
    public void ANonNumberNonNullBudgetInTheRepoFileIsAConfigExceptionNamingTheFile(string json)
    {
        FakePlatform platform = NewPlatform(repo: json);

        ConfigException ex = Assert.Throws<ConfigException>(() => Load(platform));

        Assert.Contains(repoConfigPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonNumberNonNullBudgetInTheUserFileIsAConfigExceptionNamingThatFile()
    {
        FakePlatform platform = NewPlatform(user: /*lang=json,strict*/ """{"defaults":{"budget_usd":"none"}}""");

        ConfigException ex = Assert.Throws<ConfigException>(() => Load(platform));

        Assert.Contains(userConfigPath, ex.Message, StringComparison.Ordinal);
    }

    // ---- ConfigDocument through the source-generated context ---------------------------------------------------------

    private static string Write(DefaultsSettings defaults) =>
        JsonSerializer.Serialize(new ConfigDocument(null, null, null, defaults, null), ClaustrumJsonContext.Default.ConfigDocument);

    private static ConfigDocument Read(string json) =>
        JsonSerializer.Deserialize(json, ClaustrumJsonContext.Default.ConfigDocument)!;

    [Fact]
    public void AnAbsentBudgetIsLeftOutOnWriteAndReadsBackAbsent()
    {
        string json = Write(new DefaultsSettings(TimeoutSeconds: 60, BudgetUsd: null, EnvPassthrough: null));

        Assert.DoesNotContain("budget_usd", json, StringComparison.Ordinal);
        Assert.Null(Read(json).Defaults?.BudgetUsd);
        Assert.Equal(60, Read(json).Defaults?.TimeoutSeconds);
    }

    [Fact]
    public void ANoCapBudgetWritesAnExplicitNullAndReadsBackAsNoCap()
    {
        string json = Write(new DefaultsSettings(TimeoutSeconds: null, BudgetUsd: new DefaultBudget(null), EnvPassthrough: null));

        Assert.Contains("\"budget_usd\":null", json, StringComparison.Ordinal);
        DefaultBudget? back = Read(json).Defaults?.BudgetUsd;
        Assert.NotNull(back);
        Assert.Null(back.Value.Usd);
    }

    [Fact]
    public void ANumberedBudgetWritesTheNumberAndReadsBackAsItself()
    {
        string json = Write(new DefaultsSettings(TimeoutSeconds: null, BudgetUsd: new DefaultBudget(12.5m), EnvPassthrough: null));

        Assert.Contains("\"budget_usd\":12.5", json, StringComparison.Ordinal);
        Assert.Equal(12.5m, Read(json).Defaults?.BudgetUsd?.Usd);
    }

    [Fact]
    public void AZeroBudgetWritesZeroAndReadsBackAsAZeroCap()
    {
        string json = Write(new DefaultsSettings(TimeoutSeconds: null, BudgetUsd: new DefaultBudget(0m), EnvPassthrough: null));

        Assert.Contains("\"budget_usd\":0", json, StringComparison.Ordinal);
        DefaultBudget? back = Read(json).Defaults?.BudgetUsd;
        Assert.NotNull(back);
        Assert.Equal(0m, back.Value.Usd);
    }

    [Fact]
    public void ADocumentWithoutTheKeyReadsAsSilentAndWithTheKeyNullReadsAsNoCap()
    {
        Assert.Null(Read(/*lang=json,strict*/ """{"defaults":{"timeout_seconds":5}}""").Defaults?.BudgetUsd);

        DefaultBudget? noCap = Read(/*lang=json,strict*/ """{"defaults":{"budget_usd":null}}""").Defaults?.BudgetUsd;
        Assert.NotNull(noCap);
        Assert.Null(noCap.Value.Usd);
    }

    [Fact]
    public void AStringBudgetFailsToDeserializeNamingWhatIsExpected()
    {
        JsonException ex = Assert.Throws<JsonException>(() => Read(/*lang=json,strict*/ """{"defaults":{"budget_usd":"none"}}"""));

        Assert.Contains("budget_usd", ex.Message, StringComparison.Ordinal);
    }
}
