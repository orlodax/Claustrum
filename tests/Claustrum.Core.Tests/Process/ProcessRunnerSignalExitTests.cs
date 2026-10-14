using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Tests.Process;

// A terminal's signal (or `kill -- -<pgid>`) reaches the backend, which shares Claustrum's process group, before
// CancelSignalHandler has cancelled the token: .NET reports the death as 128 + the signal (129 HUP, 130 INT, 131 QUIT, 143 TERM).
// ProcessRunner waits up to 1 s for the cancel: it arrives, it was a cancel; it does not, the backend failed. `sh -c 'exit N'` is
// that exit code exactly (the runner sees only the code), without depending on whether the test host inherited a signal as ignored.
// Unix only: on Windows exit codes carry no signal and no grace applies.
public sealed class ProcessRunnerSignalExitTests
{
    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("exit codes carry no signal on Windows, so there is no grace.");
    }

    private static JobPaths CreateTempJob()
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-signal-exit-").FullName;
        return new JobPaths("test-job", dir, Path.Combine(dir, "request.json"), Path.Combine(dir, "system.md"),
            Path.Combine(dir, "stdout.log"), Path.Combine(dir, "stderr.log"), Path.Combine(dir, "result.json"));
    }

    private static async Task<ProcessOutcome> RunAsync(int exitCode, CancellationToken cancellationToken)
    {
        JobPaths job = CreateTempJob();
        try
        {
            ProcessSpec spec = new("sh", ["-c", $"exit {exitCode}"], Path.GetTempPath(), [], []);
            return await new ProcessRunner(new RealPlatform()).RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(143)]
    public async Task ABackendKilledByASignalIsCancelledWhenTheTokenIsCancelledWithinTheGraceAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(400));

        ProcessOutcome outcome = await RunAsync(exitCode, cancel.Token);

        Assert.Equal(ProcessTermination.Cancelled, outcome.Termination);
        Assert.Equal(exitCode, outcome.ExitCode);
    }

    [Fact]
    public async Task ATokenAlreadyCancelledWhenTheBackendDiesOfASignalIsCancelledAtOnceAsync()
    {
        RequireUnix();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        // Cancelled before the run: the runner's own kill path answers, which is a cancel too.
        ProcessOutcome outcome = await RunAsync(129, cancel.Token);

        Assert.Equal(ProcessTermination.Cancelled, outcome.Termination);
    }

    [Theory]
    [InlineData(129, 2500)]
    [InlineData(143, 2500)]
    [InlineData(130, -1)]
    public async Task ABackendKilledByASignalWhoseCancelNeverComesWithinTheGraceFailedAfterAboutASecondAsync(int exitCode, int cancelAfterMilliseconds)
    {
        RequireUnix();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancelAfterMilliseconds > 0)
            cancel.CancelAfter(TimeSpan.FromMilliseconds(cancelAfterMilliseconds));

        ProcessOutcome outcome = await RunAsync(exitCode, cancel.Token);

        Assert.Equal(ProcessTermination.Completed, outcome.Termination);
        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.InRange(outcome.Duration.TotalMilliseconds, 900, 2400);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    public async Task AnOrdinaryExitCodeIsCompletedWithNoDelayAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(5));

        ProcessOutcome outcome = await RunAsync(exitCode, cancel.Token);

        Assert.Equal(ProcessTermination.Completed, outcome.Termination);
        Assert.Equal(exitCode, outcome.ExitCode);
        Assert.True(outcome.Duration.TotalMilliseconds < 900, $"took {outcome.Duration.TotalMilliseconds} ms: the grace must not apply");
    }
}
