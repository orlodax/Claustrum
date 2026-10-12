using System.Diagnostics;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #92 through the real built binary, the SIGINT file's twin (CancelSigintEndToEndTests, #88): the first SIGTERM takes the
// first-Ctrl-C path (Runner kills the backend's tree, writes the `cancelled` receipt, exit 130, the SIGTERM stderr line once);
// a second signal of either kind ends the process at once with no receipt (143 after a second SIGTERM); a SIGTERM that arrives
// while `--brief-file -` is still blocked on stdin finds no handler yet and ends the process at once; and SIGQUIT, which rides
// Console.CancelKeyPress on Unix, takes the Ctrl-C path. Each case sleeps its own odd length so a leftover `sleep` can be told
// from another case's (the series continues from CancelSigintEndToEndTests' 31.75). stdout and stderr are read as UTF-8.
public sealed class CancelSigtermEndToEndTests : IDisposable
{
    private const int SigintNumber = 2;
    private const int SigquitNumber = 3;
    private const int SigtermNumber = 15;
    private const int SigtermExit = 143;

    // Not SIGINT's 2 s: that bound tells #88's 2 s handler timeout from a prompt exit, and SIGTERM never had one (unhandled it
    // ended the process at once, with no receipt). Here the clock only separates a prompt exit from a hang; the full suite on a
    // busy machine took 2.3 s once for the coordinate case, and the receipts and exit code carry the verdict.
    private const int PromptlyMilliseconds = 5000;

    private readonly CancelScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    private static void RequirePosixSignals()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGTERM and SIGQUIT are POSIX signals; nothing here can send one to a redirected Windows child, and a registered SIGTERM handler cannot cancel the exit there (PosixSignalRegistration).");
    }

    private static int NumberOf(string signal) => signal == "TERM" ? SigtermNumber : SigintNumber;

    private Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated, int signalNumber, string signalName) =>
        scenario.StartAndWaitForPartialAsync(args, isolated, signalNumber, signalName);

    [Fact]
    public async Task SigtermOnAnIsolatedRunEndsItCancelledWithACommitHoldingTheHalfWrittenFileAsync()
    {
        RequirePosixSignals();
        const string sleep = "31.76";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, SigtermNumber, "TERM");

        await scenario.AssertCancelledAsync(cli, "TERM", CancelScenario.SigtermLine, "builder", sleep, isolated: true, PromptlyMilliseconds);
    }

    [Fact]
    public async Task SigtermOnCoordinateEndsTheArchitectCancelledWithACommitOnItsOwnBranchAsync()
    {
        RequirePosixSignals();
        const string sleep = "31.77";
        scenario.Prepare(IsolatedRepo.ForCoordinate(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["coordinate", "--brief", "do the task", "--json"], isolated: true, SigtermNumber, "TERM");

        await scenario.AssertCancelledAsync(cli, "TERM", CancelScenario.SigtermLine, "architect", sleep, isolated: true, PromptlyMilliseconds);
    }

    // The same escape hatch as a second Ctrl-C (a hanging pre-commit hook is the cheapest slow cancel path): the second
    // SIGTERM is not swallowed, the runtime ends the process, and no receipt is written. The forced exit may orphan the
    // backend and the hook, so both sleeps are registered for the fixture to kill.
    [Fact]
    public async Task ASecondSigtermEndsARunWhoseCancelPathHangsWithoutAReceiptAsync()
    {
        RequirePosixSignals();
        const string sleep = "31.78";
        const string hookSleep = "31.79";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantHangingPreCommitHook(hookSleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, SigtermNumber, "TERM");

        await cli.SendSignalAsync("TERM", TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(CancelScenario.SigtermLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the first SIGTERM alone must wait for the hanging commit");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync("TERM", TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(SigtermExit, exit);
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, CancelScenario.SigtermLine));
        AssertForcedExitLeftNoReceipt(cli);
    }

    // "Of either kind": the second signal is the runtime's default whichever it is, so a Ctrl-C after a SIGTERM (a CI cancel
    // followed by an impatient operator) and a SIGTERM after a Ctrl-C both end a hanging cancel path with no receipt.
    [Theory]
    [InlineData("TERM", "INT", "31.81", "31.82")]
    [InlineData("INT", "TERM", "31.83", "31.84")]
    public async Task ASecondSignalOfTheOtherKindForcesTheExitTooAsync(string first, string second, string sleep, string hookSleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantHangingPreCommitHook(hookSleep);
        string firstLine = first == "TERM" ? CancelScenario.SigtermLine : CancelScenario.CtrlCLine;
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, NumberOf(first), first);
        if (cli.IgnoresSignal(NumberOf(second)))
            Assert.Skip($"this test host was started with SIG{second} ignored, so it cannot reach the child.");

        await cli.SendSignalAsync(first, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(firstLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, $"the first SIG{first} alone must wait for the hanging commit");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync(second, TestContext.Current.CancellationToken);
        await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, firstLine));
        AssertForcedExitLeftNoReceipt(cli);
    }

    private void AssertForcedExitLeftNoReceipt(RunningCli cli)
    {
        string jobId = Assert.Single(scenario.Repo.JobDirectories());
        Assert.False(File.Exists(Path.Combine(scenario.Repo.JobsRoot, jobId, "result.json")), "a forced exit writes no receipt");
        Assert.Equal("", cli.Stdout.Trim());
    }

    // The brief is resolved before the handler exists (RunCommand/CoordinateCommand): with the handler first, the one signal
    // was swallowed and a stdin read that listens on no token ran on forever. Fixed, nothing is listening yet, so the
    // runtime's default ends the process (143 after SIGTERM, 130 after SIGINT) and no job is minted. Stdin is held open so
    // the read really blocks; the delay lets the child reach it (an earlier signal ends the process the same way, so a
    // slow start can only weaken this case, never fail it).
    [Theory]
    [InlineData("run", "TERM", SigtermExit)]
    [InlineData("coordinate", "TERM", SigtermExit)]
    [InlineData("run", "INT", 130)]
    public async Task ASignalWhileTheBriefIsBlockedOnStdinEndsTheProcessAtOnceAndMintsNoJobAsync(string verb, string signal, int expectedExit)
    {
        RequirePosixSignals();
        scenario.Adopt(verb == "run" ? new IsolatedRepo() : IsolatedRepo.ForCoordinate());
        string[] args = verb == "run" ? ["run", "builder", "--brief-file", "-", "--json"] : ["coordinate", "--brief-file", "-", "--json"];
        using RunningCli cli = scenario.Repo.StartCliHoldingStdin(args);
        if (cli.IgnoresSignal(NumberOf(signal)))
            Assert.Skip($"this test host was started with SIG{signal} ignored, so it cannot reach the child.");

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the child must be waiting on stdin");
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync(signal, TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(expectedExit, exit);
        Assert.Empty(scenario.Repo.JobDirectories());
        Assert.Equal("", cli.Stdout.Trim());
        Assert.DoesNotContain("cancelling", cli.Stderr, StringComparison.Ordinal);
    }

    // On Unix Console.CancelKeyPress fires for SIGQUIT as well as SIGINT, so a SIGQUIT has taken the first-Ctrl-C path since #88:
    // the receipt, exit 130 and the Ctrl-C wording (a second SIGQUIT is the runtime's default, a core dump, and is not exercised).
    [Fact]
    public async Task SigquitOnAnInPlaceRunTakesTheCtrlCPathWithAReceiptAndExit130Async()
    {
        RequirePosixSignals();
        const string sleep = "31.80";
        scenario.Prepare(new IsolatedRepo(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: false, SigquitNumber, "QUIT");

        await scenario.AssertCancelledAsync(cli, "QUIT", CancelScenario.CtrlCLine, "builder", sleep, isolated: false, PromptlyMilliseconds);

        Assert.True(File.Exists(Path.Combine(scenario.Repo.Repo, "partial.txt")));
        Assert.DoesNotContain(CancelScenario.SigtermLine, cli.Stderr, StringComparison.Ordinal);
    }
}
