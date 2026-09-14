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
}
