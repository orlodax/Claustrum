using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Coordination;

// CoordinatePlan.ToDelegateRequest re-resolves the cast from disk (CastApplication.Resolve reads
// Request.Cwd/Request.CastName, not the Cast already sitting on the plan) and reads
// AppServices.Platform.GetEnvironmentVariable("CLAUSTRUM_HOME") directly (TreeEnv) — so this needs
// both a real cast file under a temp cwd and the "AppServices home" collection, like
// DelegateEngineTests/JobManagerTests. JobPaths is hand-built rather than JobDirectory.Create()'d:
// ToDelegateRequest only ever reads job.Id, and nothing here needs a real job directory on disk.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class CoordinatePlanTests(AppServicesHomeFixture fixture) : IDisposable
{
    private static readonly JobPaths job = new("job-abc123", "", "", "", "", "", "");

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-coordinate-plan-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private CoordinatePlan Plan(CastArchitect architect, string? tierFlag = null, ConfigOverrides? overrides = null)
    {
        Cast cast = new("default", "1.0.0", architect, [], null);
        CastStore.Save(cwd, cast);

        return new CoordinatePlan(
            new CoordinateRequest(
                Cwd: cwd,
                CastName: "default",
                Issues: [],
                Brief: "do it",
                TierFlag: tierFlag,
                Overrides: overrides ?? new ConfigOverrides(),
                Stream: false,
                DiffCapBytes: 64 * 1024),
            cast,
            "default",
            "## Task\ndo it");
    }

    private static CastArchitect SpawnedCast(string? model = "claude:opus", string? tier = "xhigh") =>
        new(CastArchitect.Spawned, Model: model, Tier: tier);

    [Fact]
    public void RoleIsAlwaysArchitect()
    {
        DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

        Assert.Equal(Cast.ArchitectRole, request.Role);
    }

    [Fact]
    public void EnvCarriesTheJobIdAsTheParentJobTreeVariable()
    {
        DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

        Assert.Equal(job.Id, request.Env[BudgetLedger.TreeVariable]);
    }

    [Fact]
    public void MaxParallelIsAlwaysNull()
    {
        DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

        Assert.Null(request.MaxParallel);
    }

    [Fact]
    public void SystemAppendixNamesTheJobId()
    {
        DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

        Assert.Contains(job.Id, request.SystemAppendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TierAndModelComeFromTheCastsArchitectEntry()
    {
        DelegateRequest request = Plan(SpawnedCast(model: "claude:opus", tier: "xhigh")).ToDelegateRequest(job);

        Assert.Equal("xhigh", request.Tier);
        Assert.Equal("claude:opus", request.Overrides.Model);
    }

    [Fact]
    public void AnExplicitTierFlagStillWinsOverTheCastsArchitectEntry()
    {
        DelegateRequest request = Plan(SpawnedCast(tier: "xhigh"), tierFlag: "max").ToDelegateRequest(job);

        Assert.Equal("max", request.Tier);
    }

    [Fact]
    public void AnExplicitModelOverrideStillWinsOverTheCastsArchitectEntry()
    {
        DelegateRequest request = Plan(SpawnedCast(model: "claude:opus"), overrides: new ConfigOverrides(Model: "claude:haiku")).ToDelegateRequest(job);

        Assert.Equal("claude:haiku", request.Overrides.Model);
    }

    // HomeRedirectPlatform.GetEnvironmentVariable("CLAUSTRUM_HOME") is hardcoded to return null
    // (that class's own doc comment), so under the fixture's own platform this is the only branch
    // reachable honestly — asserting CLAUSTRUM_HOME is absent, not asserting it is present under a
    // platform rigged to never report one.
    [Fact]
    public void EnvOmitsClaustrumHomeWhenThePlatformReportsNone()
    {
        DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

        Assert.False(request.Env.ContainsKey("CLAUSTRUM_HOME"));
    }

    // The other branch of the same `if`: a platform that DOES report a CLAUSTRUM_HOME must have it
    // carried onto the architect's env. HomeRedirectPlatform can never say yes to that variable, so
    // this swaps AppServices.Platform for a platform that can, for the span of one assertion, then
    // restores the fixture's own platform — safe because xunit runs classes inside one collection
    // sequentially (AppServicesHomeFixture's own doc comment), so nothing else in this collection
    // observes the swap mid-flight.
    [Fact]
    public void EnvCarriesClaustrumHomeWhenThePlatformReportsOne()
    {
        AppServices.OverrideForTests(new ClaustrumHomeReportingPlatform("/fake/claustrum/home"));
        try
        {
            DelegateRequest request = Plan(SpawnedCast()).ToDelegateRequest(job);

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
