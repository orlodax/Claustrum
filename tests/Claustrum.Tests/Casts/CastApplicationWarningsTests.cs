using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #121: CastApplication.Resolve warns - never silently runs - a model the cast's alias only reached by falling through to a built-in
// default, nor one whose stored `resolved` no longer matches today's config. Only when the model came from the cast: a --model or
// --backend flag filled it first, and that run gets no cast-caused warning. The config it reads is AppServices.Platform's.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class CastApplicationWarningsTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-warnings-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-cast-warnings-home-").FullName;
    private readonly AppServicesScope scope;

    public CastApplicationWarningsTests() => scope = new AppServicesScope(new HomeRedirectPlatform(home));

    public void Dispose()
    {
        scope.Dispose();
        TempTree.Delete(cwd);
        TempTree.Delete(home);
    }

    private string UserPath => UserConfig.PathFor(home);

    private void SaveBuilderCast(string? model, string? resolved, string name = "default") => CastStore.Save(cwd, new Cast(
        name, "1.0.0", new CastArchitect(CastArchitect.Host),
        new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(model, Backend: null, Tier: null, Resolved: resolved) },
        BudgetUsd: null));

    private string[] Warnings(string role = "builder", ConfigOverrides? overrides = null, string? castName = null) =>
        CastApplication.Resolve(cwd, role, castName, tierFlag: null, overrides ?? new ConfigOverrides()).Warnings;

    [Fact]
    public void AnAliasThatFellThroughToABuiltInDefaultWarnsNamingWhereItLooked()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("cheap-coding", resolved: "claude:haiku");

        string[] warnings = Warnings();

        Assert.Equal(
            [$"cast 'default' role 'builder': alias 'cheap-coding' resolved to the built-in default claude:haiku; no claustrum.json or user config defines it (looked in {UserPath})"],
            warnings);
    }

    [Fact]
    public void AStoredResolutionThatDiffersFromTodaysWarnsWithBothTargetsAndTheCurrentSource()
    {
        UserConfig.Write(home, /*lang=json,strict*/ """{"models":{"cheap-coding":"claude:haiku"}}""");
        SaveBuilderCast("cheap-coding", resolved: "claude:sonnet");

        string[] warnings = Warnings();

        Assert.Equal([$"cast 'default' role 'builder': alias 'cheap-coding' resolved to claude:sonnet when the cast was created, now to claude:haiku ({UserPath})"], warnings);
    }

    [Fact]
    public void BothWarningsComeInOrderWhenTheAliasIsBuiltInAndAlsoDrifted()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("cheap-coding", resolved: "claude:sonnet");

        string[] warnings = Warnings();

        Assert.Equal(2, warnings.Length);
        Assert.Contains("resolved to the built-in default claude:haiku", warnings[0], StringComparison.Ordinal);
        Assert.Contains("resolved to claude:sonnet when the cast was created, now to claude:haiku (built-in default)", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasThatStillResolvesToWhatWasStoredWarnsOfNothing()
    {
        UserConfig.Write(home, UserConfig.Complete);
        SaveBuilderCast("cheap-coding", resolved: "claude:haiku");

        Assert.Empty(Warnings());
    }

    [Fact]
    public void ACastStoredBeforeResolvedExistedHasNothingToCompareAndWarnsOfNothingAboutDrift()
    {
        UserConfig.Write(home, UserConfig.Complete);
        SaveBuilderCast("cheap-coding", resolved: null);

        Assert.Empty(Warnings());
    }

    [Fact]
    public void AFreeFormBackendModelIdWarnsOfNothingBecauseNoAliasFellThrough()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("claude:opus", resolved: "claude:opus");

        Assert.Empty(Warnings());
    }

    // A user alias that chains to a built-in: the config does define the alias, so the warning names the built-in hop instead.
    [Fact]
    public void AUserAliasChainedToABuiltInWarnsNamingTheBuiltInHop()
    {
        UserConfig.Write(home, /*lang=json,strict*/ """{"models":{"mine":"cheap-coding"}}""");
        SaveBuilderCast("mine", resolved: "claude:haiku");

        string[] warnings = Warnings();

        Assert.Equal([$"cast 'default' role 'builder': alias 'mine' ({UserPath}) chains to the built-in default 'cheap-coding' = claude:haiku"], warnings);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("backend")]
    public void AModelOrBackendFlagFilledItFirstSoTheCastWarnsOfNothing(string flag)
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("cheap-coding", resolved: "claude:sonnet");
        ConfigOverrides overrides = flag == "model" ? new ConfigOverrides(Model: "claude:opus") : new ConfigOverrides(Backend: "claude");

        Assert.Empty(Warnings(overrides: overrides));
    }

    [Fact]
    public void ARoleWithNoModelInTheCastWarnsOfNothingHere()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast(model: null, resolved: null);

        Assert.Empty(Warnings());
    }

    [Fact]
    public void ARoleTheCastDoesNotListWarnsOfNothing()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("cheap-coding", resolved: null);

        Assert.Empty(Warnings(role: "tester"));
    }

    [Fact]
    public void NoCastAtAllWarnsOfNothing()
    {
        UserConfig.Write(home, "{}");

        Assert.Empty(Warnings());
    }

    [Fact]
    public void ANamedCastIsTheOneWarnedAbout()
    {
        UserConfig.Write(home, "{}");
        SaveBuilderCast("cheap-coding", resolved: null, name: "team");

        string[] warnings = Warnings(castName: "team");

        Assert.StartsWith("cast 'team' role 'builder': ", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchitectIsWarnedAboutFromItsOwnEntryUnderTheRoleNameArchitect()
    {
        UserConfig.Write(home, /*lang=json,strict*/ """{"models":{"frontier-reasoning":"claude:sonnet"}}""");
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "frontier-reasoning", Resolved: "claude:opus"),
            [], BudgetUsd: null));

        string[] warnings = Warnings(role: Cast.ArchitectRole);

        Assert.Equal([$"cast 'default' role 'architect': alias 'frontier-reasoning' resolved to claude:opus when the cast was created, now to claude:sonnet ({UserPath})"], warnings);
    }
}
