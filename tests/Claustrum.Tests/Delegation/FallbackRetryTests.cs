using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #126: FallbackRetry on its own, with the retry's dispatch replaced by a recorder - what the retry is built from, the receipt it leaves on the first job,
// and every reason it does not happen. The end-to-end behaviour through the built binary is FallbackRetryEndToEndTests.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class FallbackRetryTests(AppServicesHomeFixture fixture) : IDisposable
{
    private const string Fallback = "claude:opus";

    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private DelegateRequest Request(string role = "code-reviewer", string brief = "Review the diff.\n\n## Diff\n+ a line\n", decimal? budget = null) => new(
        Role: role, Brief: brief, Cwd: repo.Repo, Tier: "high",
        Overrides: new ConfigOverrides(Model: "opencode:fake/pro", BudgetUsd: budget),
        ResumeSession: null, AttachFiles: [], Env: [], Stream: false, DiffCapBytes: 64 * 1024,
        Fallback: Fallback, FallbackResolved: null);

    private static RunResult Refused(JobPaths job, decimal? cost = null, bool? beforeAnyWork = true, ErrorKind? kind = ErrorKind.ProviderQuota, RunStatus status = RunStatus.Failed) => new(
        SchemaVersion: "1", JobId: job.Id, Status: status, Backend: "opencode", Model: "fake/pro", Role: "code-reviewer", FinalMessage: "", ChangedFiles: [], Diff: null,
        DiffTruncated: false, SessionId: null, CostUsd: cost, Usage: null, ExitCode: 1, LogPath: job.StdoutLog, DurationSeconds: 1, Error: "credits", Raw: null, Report: null,
        ReportStatus: ReportStatus.Missing, Warnings: [], ErrorKind: kind, ErrorDetail: "credits", BeforeAnyWork: beforeAnyWork);

    private RunResult WriteFirst(decimal? cost = null)
    {
        JobPaths job = JobDirectory.Create(fixture.Platform);
        RunResult first = Refused(job, cost);
        File.WriteAllText(job.ResultJson, JsonSerializer.Serialize(first, ClaustrumJsonContext.Default.RunResult));
        return first;
    }

    private RunResult OnDisk(string jobId)
    {
        JobPaths paths = JobDirectory.Open(fixture.Platform, jobId) ?? throw new InvalidOperationException(jobId);
        return JsonSerializer.Deserialize(File.ReadAllText(paths.ResultJson), ClaustrumJsonContext.Default.RunResult) ?? throw new InvalidOperationException(jobId);
    }

    // ---- when it applies ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AFailedProviderSideRefusalBeforeAnyWorkQualifies()
    {
        JobPaths job = JobDirectory.Create(fixture.Platform);

        Assert.True(FallbackRetry.Qualifies(Refused(job)));
        Assert.True(FallbackRetry.Applies(Request(), Refused(job)));
    }

    [Fact]
    public void ThingsThatAreNotARefusalBeforeAnyWorkDoNotQualify()
    {
        JobPaths job = JobDirectory.Create(fixture.Platform);

        Assert.False(FallbackRetry.Qualifies(Refused(job, status: RunStatus.Success)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, status: RunStatus.Timeout)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, status: RunStatus.Cancelled)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, kind: ErrorKind.Other)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, kind: null)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, beforeAnyWork: false)));
        Assert.False(FallbackRetry.Qualifies(Refused(job, beforeAnyWork: null)));
        Assert.False(FallbackRetry.Qualifies(Refused(job) with { RetriedAs = "20260101-000000-abcdef12" }));
    }

    [Theory]
    [InlineData(ErrorKind.ProviderQuota)]
    [InlineData(ErrorKind.ProviderAuth)]
    [InlineData(ErrorKind.ModelNotFound)]
    [InlineData(ErrorKind.ProviderTransport)]
    public void EveryProviderSideKindQualifies(ErrorKind kind) => Assert.True(FallbackRetry.Qualifies(Refused(JobDirectory.Create(fixture.Platform), kind: kind)));

    [Fact]
    public void ARequestThatChoseItsModelOrContinuesASessionOrIsItselfARetryIsNeverRetried()
    {
        RunResult first = Refused(JobDirectory.Create(fixture.Platform));
        DelegateRequest request = Request();

        Assert.False(FallbackRetry.Applies(request with { Fallback = null }, first));
        Assert.False(FallbackRetry.Applies(request with { Fallback = "" }, first));
        Assert.False(FallbackRetry.Applies(request with { FallbackOf = "20260101-000000-abcdef12" }, first));
        Assert.False(FallbackRetry.Applies(request with { ResumeSession = "ses_1" }, first));
        Assert.False(FallbackRetry.Applies(request with { ParentJobId = "20260101-000000-abcdef12" }, first));
        Assert.False(FallbackRetry.Applies(request with { Pin = new ResumePin("opencode", "fake/pro", null) }, first));
        Assert.True(FallbackRetry.Applies(request, first));
    }

    [Fact]
    public async Task WhenItDoesNotApplyTheFirstReceiptComesBackUntouchedAndNothingIsPreparedAsync()
    {
        RunResult first = WriteFirst();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request() with { Fallback = null });
        bool prepareCalled = false;
        bool runCalled = false;

        RunResult returned = await FallbackRetry.RunAsync(prepared, first, request => { prepareCalled = true; return DelegateEngine.Prepare(request); }, (_, _) => { runCalled = true; return Task.FromResult(first); });

        Assert.Same(first, returned);
        Assert.False(prepareCalled);
        Assert.False(runCalled);
        Assert.Null(OnDisk(first.JobId).RetriedAs);
    }

    // ---- what the retry is made of --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRetryIsTheFirstRequestOnTheFallbackWithTheSameBriefAndTheFirstReceiptNamesItBeforeItRunsAsync()
    {
        string brief = "Review the diff.\n\n## Diff\n+ a line\n";
        RunResult first = WriteFirst();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(brief: brief));
        PreparedDelegation? seen = null;
        JobPaths? seenJob = null;
        string? retriedAsWhenItStarted = null;

        RunResult returned = await FallbackRetry.RunAsync(prepared, first, DelegateEngine.Prepare, (retry, retryJob) =>
        {
            (seen, seenJob) = (retry, retryJob);
            retriedAsWhenItStarted = OnDisk(first.JobId).RetriedAs;
            return Task.FromResult(first with { JobId = retryJob.Id, Status = RunStatus.Success, Backend = "claude", Model = "opus" });
        });

        PreparedDelegation retried = seen ?? throw new InvalidOperationException("the retry never ran");
        JobPaths retryJob = seenJob ?? throw new InvalidOperationException();
        Assert.Equal(retryJob.Id, returned.JobId);
        Assert.NotEqual(first.JobId, retryJob.Id);

        // Same brief, same role, nothing added; the model is the fallback and the request says it replaces the first and is not itself retried.
        Assert.Equal(brief, retried.Request.Brief);
        Assert.Equal("code-reviewer", retried.Request.Role);
        Assert.Equal(prepared.Request.Tier, retried.Request.Tier);
        Assert.Equal(Fallback, retried.Request.Overrides.Model);
        Assert.Null(retried.Request.Overrides.Backend);
        Assert.Null(retried.Request.Fallback);
        Assert.Equal(first.JobId, retried.Request.FallbackOf);
        Assert.Equal(first.JobId, retried.Options.FallbackOf);
        Assert.Equal("claude", retried.Role.Backend);
        Assert.Equal("opus", retried.Role.Model);

        // The warning the owner reads, and the pointer the first receipt got before the retry started (a follower can switch at once).
        string warning = Assert.Single(retried.Options.PreRunWarnings ?? [], text => text.Contains("failed before starting", StringComparison.Ordinal));
        Assert.Equal($"code-reviewer: opencode:fake/pro failed before starting (provider_quota: credits); ran on claude:opus instead (job {first.JobId})", warning);
        Assert.Equal(retryJob.Id, retriedAsWhenItStarted);
        Assert.Equal(retryJob.Id, OnDisk(first.JobId).RetriedAs);
        Assert.Equal(RunStatus.Failed, OnDisk(first.JobId).Status);
    }

    [Fact]
    public async Task ALongProviderMessageIsCutAtThreeHundredCharactersAndFlattenedAsync()
    {
        RunResult first = WriteFirst() with { ErrorDetail = "line one\n\n   line two " + new string('x', 400) };
        PreparedDelegation prepared = DelegateEngine.Prepare(Request());
        string? warning = null;

        await FallbackRetry.RunAsync(prepared, first, DelegateEngine.Prepare, (retry, retryJob) =>
        {
            warning = retry.Options.PreRunWarnings?.Single(text => text.Contains("failed before starting", StringComparison.Ordinal));
            return Task.FromResult(first);
        });

        string text = warning ?? throw new InvalidOperationException();
        Assert.Contains("(provider_quota: line one line two x", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text);
        Assert.Contains("…); ran on", text, StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("…); ran on", StringComparison.Ordinal) - text.IndexOf("provider_quota: ", StringComparison.Ordinal) - "provider_quota: ".Length, 1, 301);
    }

    // ---- the blind gate again ------------------------------------------------------------------------------------------------

    // Validate runs on the retry exactly as on the first: a retry request whose brief the gate would refuse mints nothing and leaves the first as it was
    // plus a warning. (The first request here is clean; `prepare` is handed a brief with a plan in it, as a bug that added to the brief would produce.)
    [Fact]
    public async Task TheBlindGateIsCheckedAgainOnTheRetryAndARefusalMintsNothingAsync()
    {
        RunResult first = WriteFirst();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request());
        bool ran = false;
        int jobsBefore = Directory.GetDirectories(JobDirectory.ResolveRoot(fixture.Platform)).Length;

        RunResult returned = await FallbackRetry.RunAsync(prepared, first, request => DelegateEngine.Prepare(request with { Brief = "## Plan\nstep 1" }), (_, _) =>
        {
            ran = true;
            return Task.FromResult(first);
        });

        Assert.False(ran);
        Assert.Equal(first.JobId, returned.JobId);
        Assert.Equal(RunStatus.Failed, returned.Status);
        string warning = Assert.Single(returned.Warnings);
        Assert.Equal("code-reviewer: fallback claude:opus not used: blind role: brief carries rationale", warning);
        Assert.Equal(warning, Assert.Single(OnDisk(first.JobId).Warnings));
        Assert.Null(OnDisk(first.JobId).RetriedAs);
        Assert.Equal(jobsBefore, Directory.GetDirectories(JobDirectory.ResolveRoot(fixture.Platform)).Length);
    }

    [Fact]
    public async Task ABuilderBriefIsNotBlindAndPassesTheSameGateAsync()
    {
        RunResult first = WriteFirst() with { Role = "builder" };
        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", "## Plan\nstep 1"));
        bool ran = false;

        await FallbackRetry.RunAsync(prepared, first, DelegateEngine.Prepare, (_, _) =>
        {
            ran = true;
            return Task.FromResult(first);
        });

        Assert.True(ran);
    }

    // ---- budget --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRetryGetsTheRunsBudgetLessWhatTheFirstJobCostAsync()
    {
        RunResult first = WriteFirst(cost: 0.4m);
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(budget: 1m));
        decimal? given = null;

        await FallbackRetry.RunAsync(prepared, first with { CostUsd = 0.4m }, DelegateEngine.Prepare, (retry, _) =>
        {
            given = retry.BudgetUsd;
            return Task.FromResult(first);
        });

        Assert.Equal(0.6m, given);
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.5, 0.9)]
    public async Task ABudgetTheFirstJobSpentDeclinesTheRetryAndTheFirstReceiptSaysWhyAsync(double budget, double cost)
    {
        RunResult first = WriteFirst(cost: (decimal)cost);
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(budget: (decimal)budget));
        bool ran = false;

        RunResult returned = await FallbackRetry.RunAsync(prepared, first with { CostUsd = (decimal)cost }, DelegateEngine.Prepare, (_, _) =>
        {
            ran = true;
            return Task.FromResult(first);
        });

        Assert.False(ran);
        Assert.Equal(first.JobId, returned.JobId);
        string warning = Assert.Single(returned.Warnings);
        Assert.Equal($"code-reviewer: fallback claude:opus not used: the run's budget of ${(decimal)budget} went on the first job", warning);
        Assert.Equal(warning, Assert.Single(OnDisk(first.JobId).Warnings));
        Assert.Null(OnDisk(first.JobId).RetriedAs);
    }

    // A run with no --budget still has the effective per-run cap Prepare resolved (the config's default), and that is what the first job's cost comes off.
    [Fact]
    public async Task ARunWithNoBudgetFlagHasItsDefaultCapBoundTheRetryTooAsync()
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(budget: null));
        decimal cap = prepared.BudgetUsd ?? throw new InvalidOperationException("this test needs a default per-run cap");
        RunResult first = WriteFirst(cost: 1m);
        decimal? given = null;

        await FallbackRetry.RunAsync(prepared, first with { CostUsd = 1m }, DelegateEngine.Prepare, (retry, _) =>
        {
            given = retry.BudgetUsd;
            return Task.FromResult(first);
        });

        Assert.Equal(cap - 1m, given);
    }

    // ---- a first receipt that cannot be rewritten does not stop a retry that was already decided --------------------------------

    [Fact]
    public async Task AFirstJobWhoseDirectoryIsGoneIsStillRetriedAndTheRetryIsToldWhyItsPointerIsMissingAsync()
    {
        RunResult first = Refused(JobDirectory.Create(fixture.Platform)) with { JobId = "20260101-000000-deadbeef" };
        PreparedDelegation prepared = DelegateEngine.Prepare(Request());
        string[] told = [];

        await FallbackRetry.RunAsync(prepared, first, DelegateEngine.Prepare, (retry, _) =>
        {
            told = retry.Options.PreRunWarnings ?? [];
            return Task.FromResult(first);
        });

        Assert.Contains(told, text => text.Contains("receipt could not be rewritten with retried_as: the job directory is gone", StringComparison.Ordinal));
    }
}
