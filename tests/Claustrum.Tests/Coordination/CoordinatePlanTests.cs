using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Coordination;

// CoordinatePlan.ToDelegateRequest no longer re-reads the cast from disk (issue #23: PlanAsync
// resolves Tier/Overrides/CastBudget once, pre-mint, and keeps them on the plan), but it still reads
// AppServices.Platform.GetEnvironmentVariable("CLAUSTRUM_HOME") directly (TreeEnv) — so this needs
// the "AppServices home" collection, like DelegateEngineTests/JobManagerTests. Every plan here comes
// from a real CoordinateEngine.PlanAsync call over a cast saved to a temp cwd — the same path
// production takes, and the only way Tier/Overrides/CastBudget end up resolved the way they do
// there — rather than a hand-built CoordinatePlan record.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class CoordinatePlanTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-coordinate-plan-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private async Task<CoordinatePlan> PlanAsync(CastArchitect architect, string? tierFlag = null, ConfigOverrides? overrides = null)
    {
        Cast cast = new("default", "1.0.0", architect, [], null);
        CastStore.Save(cwd, cast);

        CoordinateRequest request = new(
            Cwd: cwd,
            CastName: "default",
            Issues: [],
            Brief: "do it",
            TierFlag: tierFlag,
            Overrides: overrides ?? new ConfigOverrides(),
            Stream: false,
            DiffCapBytes: 64 * 1024);

        return await CoordinateEngine.PlanAsync(request, new NeverCalledIssueSource(), TestContext.Current.CancellationToken);
    }

    private static CastArchitect SpawnedCast(string? model = "claude:opus", string? tier = "xhigh") =>
        new(CastArchitect.Spawned, Model: model, Tier: tier);

    [Fact]
    public async Task RoleIsAlwaysArchitectAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

        Assert.Equal(Cast.ArchitectRole, request.Role);
    }

    // The job id does not exist yet (issue #23): both places that need it carry
    // DelegateRequest.JobIdToken, substituted later by PreparedDelegation.ForJob.
    [Fact]
    public async Task EnvCarriesTheJobIdTokenAsTheParentJobTreeVariableAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

        Assert.Equal(DelegateRequest.JobIdToken, request.Env[BudgetLedger.TreeVariable]);
    }

    [Fact]
    public async Task MaxParallelIsAlwaysNullAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

        Assert.Null(request.MaxParallel);
    }

    [Fact]
    public async Task SystemAppendixCarriesTheJobIdTokenAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

        Assert.Contains(DelegateRequest.JobIdToken, request.SystemAppendix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TierAndModelComeFromTheCastsArchitectEntryAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast(model: "claude:opus", tier: "xhigh"))).ToDelegateRequest();

        Assert.Equal("xhigh", request.Tier);
        Assert.Equal("claude:opus", request.Overrides.Model);
    }

    [Fact]
    public async Task AnExplicitTierFlagStillWinsOverTheCastsArchitectEntryAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast(tier: "xhigh"), tierFlag: "max")).ToDelegateRequest();

        Assert.Equal("max", request.Tier);
    }

    [Fact]
    public async Task AnExplicitModelOverrideStillWinsOverTheCastsArchitectEntryAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast(model: "claude:opus"), overrides: new ConfigOverrides(Model: "claude:haiku"))).ToDelegateRequest();

        Assert.Equal("claude:haiku", request.Overrides.Model);
    }

    // HomeRedirectPlatform.GetEnvironmentVariable("CLAUSTRUM_HOME") is hardcoded to return null
    // (that class's own doc comment), so under the fixture's own platform this is the only branch
    // reachable honestly — asserting CLAUSTRUM_HOME is absent, not asserting it is present under a
    // platform rigged to never report one.
    [Fact]
    public async Task EnvOmitsClaustrumHomeWhenThePlatformReportsNoneAsync()
    {
        DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

        Assert.False(request.Env.ContainsKey("CLAUSTRUM_HOME"));
    }

    // The other branch of the same `if`: a platform that DOES report a CLAUSTRUM_HOME must have it
    // carried onto the architect's env. HomeRedirectPlatform can never say yes to that variable, so
    // this swaps AppServices.Platform for a platform that can, for the span of one assertion, then
    // restores the fixture's own platform — safe because xunit runs classes inside one collection
    // sequentially (AppServicesHomeFixture's own doc comment), so nothing else in this collection
    // observes the swap mid-flight.
    [Fact]
    public async Task EnvCarriesClaustrumHomeWhenThePlatformReportsOneAsync()
    {
        AppServices.OverrideForTests(new ClaustrumHomeReportingPlatform("/fake/claustrum/home"));
        try
        {
            DelegateRequest request = (await PlanAsync(SpawnedCast())).ToDelegateRequest();

            Assert.Equal("/fake/claustrum/home", request.Env["CLAUSTRUM_HOME"]);
        }
        finally
        {
            // Restores the fixture's own instance, not a fresh one: every other test in this
            // collection reads AppServices.Platform expecting it to be fixture.Platform (e.g. to set
            // fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"]), so swapping in a new object here
            // would silently break that for the rest of the collection.
            AppServices.OverrideForTests(fixture.Platform);
        }
    }

    // Plan() never sets Issues, so a source that would throw if ever called proves ToDelegateRequest
    // itself touches nothing gh-shaped — the same guarantee CoordinateEngineTests' FakeIssueSource
    // gives its own callers, just phrased as "must never be called" instead of "records its calls".
    private sealed class NeverCalledIssueSource : IIssueSource
    {
        public Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PlanAsync must not consult the issue source when Issues is empty");
    }

    // Delegates everything to RealPlatform except CLAUSTRUM_HOME, which HomeRedirectPlatform hides
    // unconditionally — the one seam this test file needs and no shared fixture provides.
    private sealed class ClaustrumHomeReportingPlatform(string home) : IPlatform
    {
        private readonly RealPlatform real = new();

        public ClaustrumOs Os => real.Os;
        public string HomeDirectory => real.HomeDirectory;

        public string? GetEnvironmentVariable(string name) => name == "CLAUSTRUM_HOME" ? home : real.GetEnvironmentVariable(name);

        public IReadOnlyDictionary<string, string> GetEnvironmentVariables() => real.GetEnvironmentVariables();
        public string[] GetPathEntries() => real.GetPathEntries();
        public string[] GetPathExtensions() => real.GetPathExtensions();
        public bool FileExists(string path) => real.FileExists(path);
        public bool PathExists(string path) => real.PathExists(path);
        public string ReadAllText(string path) => real.ReadAllText(path);
    }
}
