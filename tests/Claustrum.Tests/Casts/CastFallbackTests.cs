using System.Text.Json.Nodes;
using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #126: the cast's half of the fallback model: the schema (`fallback`, `resolved_fallback`), how an answer becomes one, what the harness check says
// about one, and when CastApplication hands one to a request.
public sealed class CastFallbackTests : IDisposable
{
    private static readonly string[] roleNames = ["builder", "code-reviewer", "tester"];

    private static readonly string[] allBackends = ["claude", "api", "opencode", "copilot", "cursor"];
    private static readonly string[] someBackends = ["claude", "api", "opencode"];

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-fallback-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static Cast SpawnedWithFallbacks() => new(
        "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, "frontier-reasoning", Tier: null, Fallback: "claude:opus", ResolvedFallback: "claude:opus"),
        new Dictionary<string, CastRoleEntry?>
        {
            ["code-reviewer"] = new CastRoleEntry("opencode:openrouter/deepseek/deepseek-v4-pro", null, null, Fallback: "frontier-coding", ResolvedFallback: "claude:opus"),
            ["tester"] = null,
        },
        BudgetUsd: null);

    // ---- the schema ------------------------------------------------------------------------------------------------

    [Fact]
    public void FallbackAndResolvedFallbackRoundTripThroughTheStoreForARoleAndTheArchitect()
    {
        CastStore.Save(cwd, SpawnedWithFallbacks());

        Cast loaded = CastStore.Load(cwd, "default");

        Assert.Equal("claude:opus", loaded.Architect.Fallback);
        Assert.Equal("claude:opus", loaded.Architect.ResolvedFallback);
        Assert.Equal("frontier-coding", loaded.Roles["code-reviewer"]!.Fallback);
        Assert.Equal("claude:opus", loaded.Roles["code-reviewer"]!.ResolvedFallback);
        JsonNode written = JsonNode.Parse(File.ReadAllText(CastStore.PathFor(cwd, "default")))!;
        Assert.Equal("frontier-coding", written["roles"]!["code-reviewer"]!["fallback"]!.GetValue<string>());
        Assert.Equal("claude:opus", written["roles"]!["code-reviewer"]!["resolved_fallback"]!.GetValue<string>());
        Assert.Equal("claude:opus", written["architect"]!["fallback"]!.GetValue<string>());
        Assert.Equal("claude:opus", written["architect"]!["resolved_fallback"]!.GetValue<string>());
    }

    [Fact]
    public void ACastWrittenBeforeFallbacksExistedLoadsWithNullFallbacksEverywhere()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        File.WriteAllText(CastStore.PathFor(cwd, "old"), /*lang=json,strict*/ """
            {"name":"old","library":"1.0.0","architect":{"mode":"spawned","model":"frontier-reasoning","tier":null,"resolved":"claude:opus"},
             "roles":{"builder":{"model":"cheap-coding","backend":null,"tier":null,"max_parallel":2,"resolved":"claude:haiku"},"tester":null},"budget_usd":4}
            """);

        Cast cast = CastStore.Load(cwd, "old");

        Assert.Null(cast.Architect.Fallback);
        Assert.Null(cast.Architect.ResolvedFallback);
        Assert.Null(cast.Roles["builder"]!.Fallback);
        Assert.Null(cast.Roles["builder"]!.ResolvedFallback);
        Assert.Equal("claude:haiku", cast.Roles["builder"]!.Resolved);
    }

    // ---- resolved_fallback is recorded at creation -----------------------------------------------------------------

    [Fact]
    public void WithResolvedRecordsWhatEachFallbackResolvedToBesideTheOneOfTheModel()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"standard-coding":"opencode:vendor/model"}}""");
        Cast cast = new(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, "frontier-reasoning", Tier: null, Fallback: "cheap-coding"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = new CastRoleEntry("claude:opus", null, null, Fallback: "standard-coding"),
                ["code-reviewer"] = new CastRoleEntry("claude:opus", null, null, Fallback: "claude:sonnet"),
                ["tester"] = new CastRoleEntry("claude:opus", null, null),
                ["ui-reviewer"] = null,
            },
            BudgetUsd: null);

        Cast resolved = CastBuilder.WithResolved(cast, temp.Config);

        Assert.Equal("opencode:vendor/model", resolved.Roles["builder"]!.ResolvedFallback);
        Assert.Equal("claude:opus", resolved.Roles["builder"]!.Resolved);
        Assert.Equal("claude:sonnet", resolved.Roles["code-reviewer"]!.ResolvedFallback);
        Assert.Null(resolved.Roles["tester"]!.ResolvedFallback);
        Assert.Null(resolved.Roles["ui-reviewer"]);
        Assert.Equal("claude:haiku", resolved.Architect.ResolvedFallback);
        Assert.Equal("cheap-coding", resolved.Architect.Fallback);
    }

    [Fact]
    public void AFallbackWithNoModelOnTheRoleStillGetsItsResolvedFallback()
    {
        using TempConfig temp = new();
        Cast cast = new("default", "1.0.0", new CastArchitect(CastArchitect.Host), new Dictionary<string, CastRoleEntry?> { ["tester"] = new CastRoleEntry(null, "claude", null, Fallback: "claude:haiku") }, null);

        CastRoleEntry entry = CastBuilder.WithResolved(cast, temp.Config).Roles["tester"]!;

        Assert.Null(entry.Resolved);
        Assert.Equal("claude:haiku", entry.ResolvedFallback);
    }

    // ---- answers ---------------------------------------------------------------------------------------------------

    [Fact]
    public void FromAnswersReadsEachRolesFallbackAnswerFromItsOwnKey()
    {
        Dictionary<string, string> answers = new()
        {
            ["architect"] = "spawned on claude:opus",
            ["architect_fallback"] = " claude:sonnet ",
            ["builder"] = "claude:opus",
            ["builder_fallback"] = "opencode:x",
            ["code-reviewer"] = "claude:opus",
            ["tester"] = "claude:opus",
            ["tester_fallback"] = "none",
        };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Equal("claude:sonnet", cast.Architect.Fallback);
        Assert.Equal("opencode:x", cast.Roles["builder"]!.Fallback);
        Assert.Null(cast.Roles["code-reviewer"]!.Fallback);
        Assert.Null(cast.Roles["tester"]!.Fallback);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("")]
    [InlineData("   ")]
    public void NoneBlankOrMissingFallbackAnswersAllMeanNoFallback(string answer)
    {
        Dictionary<string, string> answers = new() { ["builder"] = "claude:opus", ["builder_fallback"] = answer };

        Assert.Null(CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers).Roles["builder"]!.Fallback);
    }

    // The 2026-10-16 rule: an answer for a role "not needed", or for a host architect, has nothing to stand in for.
    [Fact]
    public void AFallbackForARoleThatIsNotNeededOrForAHostArchitectIsDropped()
    {
        Dictionary<string, string> answers = new()
        {
            ["architect"] = "host",
            ["architect_fallback"] = "claude:opus",
            ["builder"] = "claude:opus",
            ["code-reviewer"] = "not needed",
            ["code-reviewer_fallback"] = "claude:sonnet",
        };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Null(cast.Architect.Fallback);
        Assert.Null(cast.Roles["code-reviewer"]);
    }

    // ---- the harness check -----------------------------------------------------------------------------------------

    [Fact]
    public void AFallbackOnAHarnessTheRoleDoesNotListIsRefusedNamingTheRolesFallback()
    {
        RoleLibrary library = new();
        BackendRegistry backends = new(allBackends.Select(name => new FakeBackend(name, found: true)));
        Config config = new() { Merged = new ConfigDocument(Models: [], Roles: [], Backends: [], Defaults: null, Jobs: null), Origins = [] };

        string? problem = CastHarnessCheck.Problem("ui-reviewer", "opencode:x", library, backends, config, cwd, fallback: true);

        Assert.NotNull(problem);
        Assert.Contains("puts ui-reviewer's fallback on 'opencode'", problem, StringComparison.Ordinal);
        Assert.Contains("or 'none'", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("not needed", problem, StringComparison.Ordinal);
        Assert.Null(CastHarnessCheck.Problem("ui-reviewer", "claude:sonnet", library, backends, config, cwd, fallback: true));
        Assert.Null(CastHarnessCheck.Problem("ui-reviewer", null, library, backends, config, cwd, fallback: true));
    }

    [Fact]
    public void RequireNamesEveryOffendingFallbackAlongsideTheOffendingModels()
    {
        RoleLibrary library = new();
        BackendRegistry backends = new(someBackends.Select(name => new FakeBackend(name, found: true)));
        Config config = new() { Merged = new ConfigDocument(Models: [], Roles: [], Backends: [], Defaults: null, Jobs: null), Origins = [] };
        Cast cast = new(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, "claude:opus", Fallback: "api:z"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = new CastRoleEntry("claude:opus", null, null, Fallback: "api:y"),
                ["ui-reviewer"] = new CastRoleEntry("claude:opus", null, null, Fallback: "opencode:x"),
            },
            null);

        CastException ex = Assert.Throws<CastException>(() => CastHarnessCheck.Require(cast, library, backends, config, cwd));

        Assert.Contains("puts architect's fallback on 'api'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("puts builder's fallback on 'api'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("puts ui-reviewer's fallback on 'opencode'", ex.Message, StringComparison.Ordinal);
    }

    // ---- CastApplication hands the fallback to a request ------------------------------------------------------------

    [Fact]
    public void CastApplicationSurfacesTheRolesFallbackAndItsResolvedOnlyWhenTheCastChoseTheModel()
    {
        CastStore.Save(cwd, SpawnedWithFallbacks());

        CastApplicationResult byCast = CastApplication.Resolve(cwd, "code-reviewer", castName: null, tierFlag: null, new ConfigOverrides());
        CastApplicationResult byModelFlag = CastApplication.Resolve(cwd, "code-reviewer", castName: null, tierFlag: null, new ConfigOverrides(Model: "claude:haiku"));
        CastApplicationResult byBackendFlag = CastApplication.Resolve(cwd, "code-reviewer", castName: null, tierFlag: null, new ConfigOverrides(Backend: "claude"));

        Assert.Equal("frontier-coding", byCast.Fallback);
        Assert.Equal("claude:opus", byCast.FallbackResolved);
        Assert.Null(byModelFlag.Fallback);
        Assert.Null(byModelFlag.FallbackResolved);
        Assert.Null(byBackendFlag.Fallback);
    }

    [Fact]
    public void CastApplicationSurfacesTheSpawnedArchitectsFallback()
    {
        CastStore.Save(cwd, SpawnedWithFallbacks());

        CastApplicationResult applied = CastApplication.Resolve(cwd, Cast.ArchitectRole, castName: null, tierFlag: null, new ConfigOverrides());

        Assert.Equal("claude:opus", applied.Fallback);
        Assert.Equal("claude:opus", applied.FallbackResolved);
    }

    [Fact]
    public void ARoleWithoutAFallbackSurfacesNone()
    {
        CastStore.Save(cwd, SpawnedWithFallbacks());

        Assert.Null(CastApplication.Resolve(cwd, "tester", castName: null, tierFlag: null, new ConfigOverrides()).Fallback);
        Assert.Null(CastApplication.Resolve(cwd, "builder", castName: null, tierFlag: null, new ConfigOverrides()).Fallback);
    }
}
