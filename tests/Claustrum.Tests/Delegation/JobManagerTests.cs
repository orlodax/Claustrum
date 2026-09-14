using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Delegation;

namespace Claustrum.Tests.Delegation;

// Exercises the real AppServices singletons end to end (no fakes: JobManager/DelegateEngine read
// AppServices directly, same as RunCommand did before the extraction) against backend "nonexistent",
// which Runner rejects immediately with Status.BackendMissing — fast, and never touches git or
// spawns a real process, so this is safe to run without a `claude` install.
public sealed class JobManagerTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-jobmanager-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private DelegateRequest MissingBackendRequest() => new(
        Role: "builder",
        Brief: "hi",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "nonexistent"),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024);

    // code-reviewer is blind (see list_roles); "## Plan" trips EnsureBlindGate before any process
    // spawns, so — like MissingBackendRequest — this faults synchronously and needs no real backend.
    private DelegateRequest BlindGateViolationRequest() => new(
        Role: "code-reviewer",
        Brief: "## Plan\nstep 1",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024);

    [Fact]
    public async Task StartReturnsAJobIdImmediatelyAndTheResultEventuallyResolvesAsync()
    {
        JobManager manager = new();

        (string jobId, string logPath) = manager.Start(MissingBackendRequest(), CancellationToken.None);

        Assert.NotEmpty(jobId);
        Assert.EndsWith("stdout.log", logPath, StringComparison.Ordinal);

        RunResult? result = await manager.GetResultAsync(jobId);
        Assert.NotNull(result);
        Assert.Equal(RunStatus.BackendMissing, result!.Status);
        Assert.Equal(jobId, result.JobId);
    }

    [Fact]
    public async Task GetStatusReportsDoneOnceTheTaskCompletesAsync()
    {
        JobManager manager = new();
        (string jobId, _) = manager.Start(MissingBackendRequest(), CancellationToken.None);

        await manager.GetResultAsync(jobId);
        JobStatusInfo? status = manager.GetStatus(jobId);

        Assert.NotNull(status);
        Assert.Equal("done", status!.State);
    }

    [Fact]
    public void GetStatusForAnUnknownJobIdReturnsNull()
    {
        JobManager manager = new();

        Assert.Null(manager.GetStatus("does-not-exist"));
    }

    [Fact]
    public async Task GetResultForAnUnknownJobIdReturnsNullAsync()
    {
        JobManager manager = new();

        Assert.Null(await manager.GetResultAsync("does-not-exist"));
    }

    // Finding #6: a job whose task faults before producing a RunResult (e.g. a blind-gate
    // rejection) must be distinguishable from both "running" and "done" — GetStatus used to report
    // "done" here, so a polling caller saw apparent success.
    [Fact]
    public async Task GetStatusReportsFailedWhenTheJobsTaskFaultsAsync()
    {
        JobManager manager = new();
        (string jobId, _) = manager.Start(BlindGateViolationRequest(), CancellationToken.None);

        JobStatusInfo? status = manager.GetStatus(jobId);
        Assert.Equal("failed", status?.State);

        // Observe the task's exception so nothing reports it unobserved later.
        await Assert.ThrowsAsync<BlindGateException>(() => manager.GetResultAsync(jobId));
    }

    [Fact]
    public async Task GetResultAsyncRethrowsTheUnderlyingExceptionForAFaultedJobAsync()
    {
        JobManager manager = new();
        (string jobId, _) = manager.Start(BlindGateViolationRequest(), CancellationToken.None);

        await Assert.ThrowsAsync<BlindGateException>(() => manager.GetResultAsync(jobId));
    }

    // ResolveTaskAsync is the tri-state decision GetResultAsync delegates to (still running / done /
    // faulted); it is tested directly against a hand-built TaskCompletionSource because the
    // AppServices-backed Start() path above has no seam to force a genuinely "still running" task
    // without either flaky timing or spawning a real backend process.
    [Fact]
    public async Task ResolveTaskAsyncReturnsNullWithoutAwaitingAStillRunningTaskAsync()
    {
        TaskCompletionSource<RunResult> pending = new();

        Task<RunResult?> resolved = JobManager.ResolveTaskAsync(pending.Task);

        Assert.True(resolved.IsCompleted);
        Assert.Null(await resolved);
    }

    [Fact]
    public async Task ResolveTaskAsyncReturnsTheResultOfACompletedTaskAsync()
    {
        RunResult expected = DummyResult();
        TaskCompletionSource<RunResult> completed = new();
        completed.SetResult(expected);

        Assert.Same(expected, await JobManager.ResolveTaskAsync(completed.Task));
    }

    [Fact]
    public async Task ResolveTaskAsyncRethrowsTheExceptionOfAFaultedTaskAsync()
    {
        TaskCompletionSource<RunResult> faulted = new();
        faulted.SetException(new BlindGateException("blind role: brief carries rationale"));

        await Assert.ThrowsAsync<BlindGateException>(() => JobManager.ResolveTaskAsync(faulted.Task));
    }

    private static RunResult DummyResult() => new(
        SchemaVersion: "1", JobId: "job-1", Status: RunStatus.Success, Backend: "claude", Model: "opus",
        Role: "builder", FinalMessage: "", ChangedFiles: [], Diff: null, DiffTruncated: false,
        SessionId: null, CostUsd: null, Usage: null, ExitCode: 0, LogPath: "log", DurationSeconds: 0,
        Error: null, Raw: null, Report: null, ReportStatus: ReportStatus.Missing, Warnings: []);
}
