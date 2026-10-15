using Claustrum.Casts;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #121: the model table and the warnings share one set of builders, so a cast saved now and a run later say the same words.
public sealed class CastModelLinesTests
{
    private const string Builtin = "built-in default — no claustrum.json or user config defines it";

    [Fact]
    public void ABuiltInAliasLineSaysNoConfigDefinesIt()
    {
        using TempConfig temp = new();

        string line = CastModelLines.ModelLine("cheap-coding", temp.Config.Describe("cheap-coding"));

        Assert.Equal($"cheap-coding → claude:haiku ({Builtin})", line);
    }

    [Fact]
    public void AUserAliasLineNamesTheUserConfigFile()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"cheap-coding":"opencode:vendor/model"}}""");

        Assert.Equal($"cheap-coding → opencode:vendor/model ({temp.UserPath})", CastModelLines.ModelLine("cheap-coding", temp.Config.Describe("cheap-coding")));
    }

    [Fact]
    public void ARepoAliasLineNamesTheRepoFile()
    {
        using TempConfig temp = new(repoJson: /*lang=json,strict*/ """{"models":{"fast":"claude:from-repo"}}""");

        Assert.Equal($"fast → claude:from-repo ({temp.RepoPath})", CastModelLines.ModelLine("fast", temp.Config.Describe("fast")));
    }

    [Fact]
    public void AFreeFormSpecLineSaysItWasTypedAsIs()
    {
        using TempConfig temp = new();

        Assert.Equal("claude:opus → claude:opus (free-form, as typed)", CastModelLines.ModelLine("claude:opus", temp.Config.Describe("claude:opus")));
    }

    [Fact]
    public void TheBuiltInWarningNamesTheRoleTheAliasTheTargetAndWhereItLooked()
    {
        using TempConfig temp = new();

        string warning = CastModelLines.BuiltinDefaultWarning("default", "builder", temp.Config.Describe("cheap-coding"), temp.Config);

        Assert.Equal(
            $"cast 'default' role 'builder': alias 'cheap-coding' resolved to the built-in default claude:haiku; no claustrum.json or user config defines it (looked in {temp.UserPath})",
            warning);
    }

    [Fact]
    public void TheBuiltInWarningLooksInTheRepoFileTooWhenThereIsAGitRoot()
    {
        using TempConfig temp = new(repoJson: "{}");

        string warning = CastModelLines.BuiltinDefaultWarning("team", "tester", temp.Config.Describe("fast"), temp.Config);

        Assert.EndsWith($"(looked in {temp.UserPath}, {temp.RepoPath})", warning, StringComparison.Ordinal);
        Assert.StartsWith("cast 'team' role 'tester': alias 'fast' ", warning, StringComparison.Ordinal);
    }

    // The ModelResolution parameter replaced a bare Spec string: a user alias that CHAINS to a built-in names the built-in hop,
    // because "no config defines it" would be false for the alias the user did write.
    [Fact]
    public void AUserAliasChainedToABuiltInNamesTheBuiltInHopNotAMissingDefinition()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"mine":"cheap-coding"}}""");

        string warning = CastModelLines.BuiltinDefaultWarning("default", "builder", temp.Config.Describe("mine"), temp.Config);

        Assert.Equal($"cast 'default' role 'builder': alias 'mine' ({temp.UserPath}) chains to the built-in default 'cheap-coding' = claude:haiku", warning);
        Assert.DoesNotContain("no claustrum.json or user config defines it", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDriftWarningNamesBothTargetsAndTheSourceOfTheCurrentOne()
    {
        string warning = CastModelLines.ResolvedChangedWarning("default", "builder", "cheap-coding", "claude:sonnet", "claude:haiku", "/home/me/.config/claustrum/config.json");

        Assert.Equal("cast 'default' role 'builder': alias 'cheap-coding' resolved to claude:sonnet when the cast was created, now to claude:haiku (/home/me/.config/claustrum/config.json)", warning);
    }

    [Fact]
    public void ForCastGivesOneLinePerRoleWithAModelPlusTheArchitectAndAWarningPerBuiltInHop()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"standard-coding":"claude:sonnet","mine":"fast"}}""");
        Cast cast = new(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "frontier-reasoning"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = new CastRoleEntry("standard-coding", null, null),
                ["code-reviewer"] = new CastRoleEntry("claude:opus", null, null),
                ["tester"] = new CastRoleEntry("mine", null, null),
                ["ui-reviewer"] = null,
                ["demo-author"] = new CastRoleEntry(null, "claude", null),
            },
            BudgetUsd: null);

        (string[] lines, string[] warnings) = CastModelLines.ForCast(cast, temp.Config);

        Assert.Equal(
        [
            $"standard-coding → claude:sonnet ({temp.UserPath})",
            "claude:opus → claude:opus (free-form, as typed)",
            $"mine → claude:haiku ({temp.UserPath})",
            $"frontier-reasoning → claude:opus ({Builtin})",
        ],
            lines);
        Assert.Equal(2, warnings.Length);
        Assert.Contains("role 'tester': alias 'mine'", warnings[0], StringComparison.Ordinal);
        Assert.Contains("chains to the built-in default 'fast'", warnings[0], StringComparison.Ordinal);
        Assert.Contains("role 'architect': alias 'frontier-reasoning' resolved to the built-in default claude:opus", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ForCastWithOnlyDefinedAliasesWarnsOfNothing()
    {
        using TempConfig temp = new(userJson: UserConfig.Complete);
        Cast cast = new("default", "1.0.0", new CastArchitect(CastArchitect.Host), new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry("cheap-coding", null, null) }, null);

        (string[] lines, string[] warnings) = CastModelLines.ForCast(cast, temp.Config);

        Assert.Equal([$"cheap-coding → claude:haiku ({temp.UserPath})"], lines);
        Assert.Empty(warnings);
    }

    // ---- what a cast remembers: CastBuilder.WithResolved ----------------------------------------------------------

    [Fact]
    public void WithResolvedStoresTheTargetEachModelResolvedToAtCreationTime()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"standard-coding":"opencode:vendor/model"}}""");
        Cast cast = new(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "cheap-coding", Tier: "xhigh"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = new CastRoleEntry("standard-coding", null, "high", MaxParallel: 2),
                ["code-reviewer"] = new CastRoleEntry("claude:opus", null, null),
                ["tester"] = new CastRoleEntry(null, "claude", null),
                ["ui-reviewer"] = null,
            },
            BudgetUsd: 3m);

        Cast resolved = CastBuilder.WithResolved(cast, temp.Config);

        Assert.Equal(new CastRoleEntry("standard-coding", null, "high", MaxParallel: 2, Resolved: "opencode:vendor/model"), resolved.Roles["builder"]);
        Assert.Equal("claude:opus", resolved.Roles["code-reviewer"]!.Resolved);
        Assert.Null(resolved.Roles["tester"]!.Resolved);
        Assert.Null(resolved.Roles["ui-reviewer"]);
        Assert.Equal(new CastArchitect(CastArchitect.Spawned, "cheap-coding", "xhigh", Resolved: "claude:haiku"), resolved.Architect);
        Assert.Equal(3m, resolved.BudgetUsd);
    }

    [Fact]
    public void AHostArchitectWithNoModelStaysWithNoResolved()
    {
        using TempConfig temp = new();
        Cast cast = new("default", "1.0.0", new CastArchitect(CastArchitect.Host), [], null);

        Assert.Equal(new CastArchitect(CastArchitect.Host), CastBuilder.WithResolved(cast, temp.Config).Architect);
    }
}
