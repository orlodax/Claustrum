using Claustrum.Casts;
using Claustrum.Cli;
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

    // ---- #74: the architect's own worktree ----------------------------------------------------------------------

    private static CoordinateRequest RequestAt(string cwd, string brief = "do it", int[]? issues = null) => new(
        Cwd: cwd,
        CastName: "default",
        Issues: issues ?? [],
        Brief: issues is { Length: > 0 } ? null : brief,
        TierFlag: null,
        Overrides: new ConfigOverrides(),
        Stream: false,
        DiffCapBytes: 64 * 1024);

    private static async Task<CoordinatePlan> PlanInAsync(IsolatedRepo repo, string brief = "do it") =>
        await CoordinateEngine.PlanAsync(RequestAt(repo.Repo, brief), new NeverCalledIssueSource(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task OutsideAGitRepositoryThePlanIsNotIsolatedAndTheRequestAsksForNoWorktreeAsync()
    {
        CoordinatePlan plan = await PlanAsync(SpawnedCast());

        Assert.False(plan.Isolated);
        Assert.Empty(plan.Warnings);
        DelegateRequest request = plan.ToDelegateRequest();
        Assert.False(request.Isolate);
        Assert.Contains($"No git repository at {cwd}:", request.SystemAppendix, StringComparison.Ordinal);
        Assert.Contains($"- Working directory: {cwd}\n", plan.UserPrompt + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task InACommittedRepositoryThePlanIsIsolatedAndEverythingItProducesAgreesAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();

        CoordinatePlan plan = await PlanInAsync(repo);

        string token = Path.Combine(repo.Repo, ".claustrum", "worktrees", DelegateRequest.JobIdToken);
        Assert.True(plan.Isolated);
        Assert.Empty(plan.Warnings);
        DelegateRequest request = plan.ToDelegateRequest();
        Assert.True(request.Isolate);
        Assert.Null(request.MaxParallel);
        Assert.Equal(repo.Repo, request.Cwd);
        Assert.Contains($"Work branch: claustrum/{DelegateRequest.JobIdToken} — you are already on it, in your own worktree {token}", request.SystemAppendix, StringComparison.Ordinal);
        Assert.Contains($"- Working directory: {token}", request.Brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlanCarriesTheWarningsOfAHarnessConfigWithALocalEditAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        File.WriteAllText(Path.Combine(repo.Repo, "opencode.json"), "{}\n");
        repo.CommitAll("harness config");
        File.AppendAllText(Path.Combine(repo.Repo, "opencode.json"), "\n");

        CoordinatePlan plan = await PlanInAsync(repo);

        Assert.True(plan.Isolated);
        Assert.Equal(["opencode.json (uncommitted changes): the architect's worktree gets HEAD's copy — your local edit stays out of it"], plan.Warnings);
    }

    // Binding the job id into the user prompt must touch that one line and no other: the task above it is the caller's
    // text or an issue's, and a brief about this very code quotes `{{job_id}}` itself (NOTES.md "What the architect is told").
    [Fact]
    public async Task BindUserPromptReplacesOnlyTheWorkingDirectoryLineAndLeavesATaskQuotingTheTokenVerbatimAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        const string task = "document how `{{job_id}}` is filled in\n\nand `Delegate with: --cwd .../worktrees/{{job_id}}` too";
        CoordinatePlan plan = await PlanInAsync(repo, task);
        PreparedDelegation prepared = plan.Prepare();

        PreparedDelegation bound = plan.BindUserPrompt(prepared, "20261009-120000-abcdef12");

        string[] before = prepared.Request.Brief.Split('\n');
        string[] after = bound.Request.Brief.Split('\n');
        Assert.Equal(before.Length, after.Length);
        int[] changed = [.. Enumerable.Range(0, before.Length).Where(index => before[index] != after[index])];
        int line = Assert.Single(changed);
        Assert.Equal($"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", DelegateRequest.JobIdToken)}", before[line]);
        Assert.Equal($"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", "20261009-120000-abcdef12")}", after[line]);
        Assert.Contains("document how `{{job_id}}` is filled in", bound.Request.Brief, StringComparison.Ordinal);
        Assert.Contains("--cwd .../worktrees/{{job_id}}` too", bound.Request.Brief, StringComparison.Ordinal);
        // Everything else about the delegation is the same object: only the brief was rebuilt.
        Assert.Same(prepared.Role, bound.Role);
        Assert.Equal(prepared.Request.Env, bound.Request.Env);
        Assert.Equal(prepared.Request.SystemAppendix, bound.Request.SystemAppendix);
    }

    // #89: a task that quotes the very line BindUserPrompt looks for — the one a brief about this code would — must
    // come through verbatim; only the line of the `## Context` block Claustrum wrote is bound.
    [Fact]
    public async Task BindUserPromptLeavesATaskQuotingTheExactWorkingDirectoryLineVerbatimAndBindsTheContextLineAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        const string jobId = "20261009-120000-abcdef12";
        string quoted = $"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", DelegateRequest.JobIdToken)}";
        string task = $"the brief ends with\n\n{quoted}\n\nand must keep saying so";
        CoordinatePlan plan = await PlanInAsync(repo, task);
        PreparedDelegation prepared = plan.Prepare();

        PreparedDelegation bound = plan.BindUserPrompt(prepared, jobId);

        string brief = bound.Request.Brief;
        int context = brief.LastIndexOf("\n## Context\n", StringComparison.Ordinal);
        Assert.StartsWith($"## Task\n{task}\n\n## Context\n", brief, StringComparison.Ordinal);
        Assert.Contains(quoted, brief[..context], StringComparison.Ordinal);
        Assert.DoesNotContain(DelegateRequest.JobIdToken, brief[context..], StringComparison.Ordinal);
        Assert.Contains($"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", jobId)}", brief[context..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindUserPromptKeepsATaskWithItsOwnContextHeadingAndPlaceholderLineAsTheCallerWroteItAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        string quoted = $"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", DelegateRequest.JobIdToken)}";
        string task = $"paste this into the doc:\n\n## Context\n{quoted}\n\nthen run `{{{{job_id}}}}` through it";
        CoordinatePlan plan = await PlanInAsync(repo, task);
        PreparedDelegation prepared = plan.Prepare();

        PreparedDelegation bound = plan.BindUserPrompt(prepared, "20261009-120000-abcdef12");

        string brief = bound.Request.Brief;
        Assert.StartsWith($"## Task\n{task}\n\n## Context\n", brief, StringComparison.Ordinal);
        Assert.Equal(1, brief.Split(quoted).Length - 1);
        Assert.Equal(1, brief.Split($"- Working directory: {Path.Combine(repo.Repo, ".claustrum", "worktrees", "20261009-120000-abcdef12")}").Length - 1);
        Assert.Contains("then run `{{job_id}}` through it", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindUserPromptRefusesABriefWithoutTheContextBlockInsteadOfBindingNothingAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        CoordinatePlan plan = await PlanInAsync(repo);
        PreparedDelegation prepared = plan.Prepare();
        PreparedDelegation headless = prepared with { Request = prepared.Request with { Brief = "no headings at all" } };

        Assert.Throws<InvalidOperationException>(() => plan.BindUserPrompt(headless, "20261009-120000-abcdef12"));
    }

    [Fact]
    public async Task PreparedDelegationForJobFillsTheTokenInTheSystemPromptButNeverInTheBriefAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        CoordinatePlan plan = await PlanInAsync(repo, "quote {{job_id}}");

        PreparedDelegation filled = plan.Prepare().ForJob("20261009-120000-abcdef12");

        Assert.Contains("Job tree: 20261009-120000-abcdef12", filled.Role.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(DelegateRequest.JobIdToken, filled.Role.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("quote {{job_id}}", filled.Request.Brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindUserPromptOfAPlanThatIsNotIsolatedReturnsTheSameInstanceAsync()
    {
        CoordinatePlan plan = await PlanAsync(SpawnedCast());
        PreparedDelegation prepared = plan.Prepare();

        PreparedDelegation bound = plan.BindUserPrompt(prepared, "20261009-120000-abcdef12");

        Assert.Same(prepared, bound);
    }

    // The refusal comes before `gh`: an architect that cannot run in a worktree must not cost a network round trip.
    [Fact]
    public async Task AnUncommittedClaustrumJsonIsRefusedBeforeTheIssueSourceIsAskedAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        File.AppendAllText(Path.Combine(repo.Repo, "claustrum.json"), "\n");
        RecordingIssueSource issues = new();

        CliUsageException ex = await Assert.ThrowsAsync<CliUsageException>(
            () => CoordinateEngine.PlanAsync(RequestAt(repo.Repo, issues: [12, 13]), issues, TestContext.Current.CancellationToken));

        Assert.Contains("commit (or un-ignore) claustrum.json (uncommitted changes) first", ex.Message, StringComparison.Ordinal);
        Assert.Empty(issues.Numbers);
    }

    [Fact]
    public async Task AReadyRepositoryAsksTheIssueSourceInTheOrderGivenAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        RecordingIssueSource issues = new();

        CoordinatePlan plan = await CoordinateEngine.PlanAsync(RequestAt(repo.Repo, issues: [12, 3]), issues, TestContext.Current.CancellationToken);

        Assert.Equal([12, 3], issues.Numbers);
        Assert.True(plan.Isolated);
        Assert.Contains("Issues: #12, #3.", plan.UserPrompt, StringComparison.Ordinal);
    }

    // Plan() never sets Issues, so a source that would throw if ever called proves ToDelegateRequest
    // itself touches nothing gh-shaped — the same guarantee CoordinateEngineTests' FakeIssueSource
    // gives its own callers, just phrased as "must never be called" instead of "records its calls".
    private sealed class NeverCalledIssueSource : IIssueSource
    {
        public Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("PlanAsync must not consult the issue source when Issues is empty");
    }

    private sealed class RecordingIssueSource : IIssueSource
    {
        public List<int> Numbers { get; } = [];

        public Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken)
        {
            Numbers.Add(number);
            return Task.FromResult(new GhIssue(number, $"issue {number}", "body", [], $"https://example.invalid/{number}"));
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
