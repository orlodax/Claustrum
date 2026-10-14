using System.Diagnostics;
using Claustrum.Core.Process;

namespace Claustrum.Core.Tests.Process;

// Every git/gh child made under a run's token shares the terminal's process group, so a terminal's SIGHUP/SIGINT (or
// `kill -- -<pgid>`) can kill it before the token is cancelled: .NET reports 128 + the signal. CommandProcess is where that is
// read, once, for all callers (it used to be a patch in one of them): with a cancellable token, a signal exit waits up to 1 s for
// the cancel - it arrives, it is an OperationCanceledException; it does not, the tool's own exit code is returned. A token that
// can never be cancelled, and any ordinary exit code, are not delayed. `sh -c 'exit N'` is that exit code exactly.
// Unix only: on Windows exit codes carry no signal.
public sealed class CommandProcessSignalExitTests
{
    private static readonly TimeSpan bound = TimeSpan.FromSeconds(30);

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("exit codes carry no signal on Windows, so there is no grace.");
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(int exitCode, CancellationToken token) =>
        CommandProcess.RunAsync("sh", Path.GetTempPath(), ["-c", $"exit {exitCode}"], bound, token);

    [Theory]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(143)]
    public async Task ASignalExitWithATokenCancelledNowIsACancelAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        // A token already cancelled before the call is cancelled by the process machinery itself; either way it is a cancel.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(exitCode, cancel.Token));
    }

    [Theory]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(143)]
    public async Task ASignalExitWithATokenCancelledWithinTheGraceIsACancelAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(exitCode, cancel.Token));
    }

    [Theory]
    [InlineData(129)]
    [InlineData(143)]
    public async Task ASignalExitWhoseCancelNeverComesReturnsTheCodeAfterAboutASecondAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource neverCancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();

        (int code, _, _) = await RunAsync(exitCode, neverCancelled.Token);

        Assert.Equal(exitCode, code);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 900, 3000);
    }

    [Fact]
    public async Task ACancelThatComesAfterTheGraceIsTooLateAndTheCodeIsReturnedAsync()
    {
        RequireUnix();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(2500));

        (int code, _, _) = await RunAsync(130, cancel.Token);

        Assert.Equal(130, code);
    }

    [Fact]
    public async Task ATokenThatCanNeverBeCancelledGetsTheCodeAtOnceAsync()
    {
        RequireUnix();
        Stopwatch stopwatch = Stopwatch.StartNew();

        (int code, _, _) = await RunAsync(129, CancellationToken.None);

        Assert.Equal(129, code);
        Assert.True(stopwatch.ElapsedMilliseconds < 900, $"took {stopwatch.ElapsedMilliseconds} ms: no grace applies to a token that cannot be cancelled");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    public async Task AnOrdinaryExitCodeIsNotDelayedEvenWithACancellableTokenAsync(int exitCode)
    {
        RequireUnix();
        using CancellationTokenSource cancellable = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Stopwatch stopwatch = Stopwatch.StartNew();

        (int code, _, _) = await RunAsync(exitCode, cancellable.Token);

        Assert.Equal(exitCode, code);
        Assert.True(stopwatch.ElapsedMilliseconds < 900, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ASuccessfulCallWithACancelledTokenIsUnaffectedByTheSignalMappingAsync()
    {
        RequireUnix();
        using CancellationTokenSource cancellable = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        (int code, string stdout, _) = await CommandProcess.RunAsync("sh", Path.GetTempPath(), ["-c", "echo done; exit 0"], bound, cancellable.Token);

        Assert.Equal(0, code);
        Assert.Equal("done", stdout.Trim());
    }
}
