using System.Diagnostics;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #111 through the real built binary, the SIGTERM file's twin (CancelSigtermEndToEndTests, #92): a closed terminal sends SIGHUP,
// which used to end the process with no receipt (exit 129). Now the first one takes the first-Ctrl-C path (Runner kills
// the backend's tree, writes the `cancelled` receipt, exit 130, the SIGHUP stderr line once); a second signal of any kind ends the
// process at once with no receipt (129 after a second SIGHUP); a SIGHUP while `--brief-file -` is still blocked on stdin finds no
// handler yet and ends the process with the default action. Each case sleeps its own odd length (the series continues from
// 32.10 after the SIGTERM file's 31.xx). POSIX only: nothing can send SIGHUP to a redirected Windows child.
public sealed class CancelSighupEndToEndTests : IDisposable
{
    private const int SighupNumber = 1;
    private const int SigintNumber = 2;
    private const int SigtermNumber = 15;
    private const int SighupExit = 129;

    // Only separates a prompt exit from a hang (the receipts and exit codes carry the verdict), as in the SIGTERM file.
    private const int PromptlyMilliseconds = 5000;

    private readonly CancelScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    private static void RequirePosixSignals()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGHUP is a POSIX signal; nothing here can send one to a redirected Windows child (it is the console-close event there).");
    }

    private static int NumberOf(string signal) => signal switch
    {
        "HUP" => SighupNumber,
        "TERM" => SigtermNumber,
        _ => SigintNumber,
    };

    private static string LineOf(string signal) => signal switch
    {
        "HUP" => CancelScenario.SighupLine,
        "TERM" => CancelScenario.SigtermLine,
        _ => CancelScenario.CtrlCLine,
    };

    private Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated) =>
        scenario.StartAndWaitForPartialAsync(args, isolated, SighupNumber, "HUP");

    [Fact]
    public async Task SighupOnAnIsolatedRunEndsItCancelledWithACommitHoldingTheHalfWrittenFileAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.11";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await scenario.AssertCancelledAsync(cli, "HUP", CancelScenario.SighupLine, "builder", sleep, isolated: true, PromptlyMilliseconds);

        Assert.DoesNotContain(CancelScenario.SigtermLine, cli.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(CancelScenario.CtrlCLine, cli.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SighupOnAnInPlaceRunEndsItCancelledAndKeepsTheHalfWrittenFileAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.12";
        scenario.Prepare(new IsolatedRepo(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: false);

        await scenario.AssertCancelledAsync(cli, "HUP", CancelScenario.SighupLine, "builder", sleep, isolated: false, PromptlyMilliseconds);

        Assert.True(File.Exists(Path.Combine(scenario.Repo.Repo, "partial.txt")));
    }

    [Fact]
    public async Task SighupOnCoordinateEndsTheArchitectCancelledWithACommitOnItsOwnBranchAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.13";
        scenario.Prepare(IsolatedRepo.ForCoordinate(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["coordinate", "--brief", "do the task", "--json"], isolated: true);

        await scenario.AssertCancelledAsync(cli, "HUP", CancelScenario.SighupLine, "architect", sleep, isolated: true, PromptlyMilliseconds);
    }

    // A second SIGHUP is not swallowed: the runtime ends the process (129 = 128 + 1) while the cancel path hangs in the repository's
    // pre-commit hook, and no receipt is written. The forced exit may orphan the backend and the hook; both sleeps are registered for
    // the fixture to kill.
    [Fact]
    public async Task ASecondSighupEndsARunWhoseCancelPathHangsWithoutAReceiptAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.14";
        const string hookSleep = "32.15";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantHangingPreCommitHook(hookSleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await cli.SendSignalAsync("HUP", TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(CancelScenario.SighupLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the first SIGHUP alone must wait for the hanging commit");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync("HUP", TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(SighupExit, exit);
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, CancelScenario.SighupLine));
        AssertForcedExitLeftNoReceipt(cli);
    }

    // "Of any kind": whichever signal follows the first, the runtime's default ends a hanging cancel path with no receipt.
    [Theory]
    [InlineData("HUP", "INT", "32.16", "32.17")]
    [InlineData("INT", "HUP", "32.18", "32.19")]
    [InlineData("HUP", "TERM", "32.20", "32.21")]
    [InlineData("TERM", "HUP", "32.22", "32.23")]
    public async Task ASecondSignalOfAnotherKindForcesTheExitTooAsync(string first, string second, string sleep, string hookSleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantHangingPreCommitHook(hookSleep);
        string firstLine = LineOf(first);
        using RunningCli cli = await scenario.StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, NumberOf(first), first);
        if (cli.IgnoresSignal(NumberOf(second)))
            Assert.Skip($"this test host was started with SIG{second} ignored, so it cannot reach the child.");

        await cli.SendSignalAsync(first, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(firstLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
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

    // The brief is resolved before the handler exists (RunCommand/CoordinateCommand), so a SIGHUP while a stdin read blocks finds
    // nothing listening and the runtime's default ends the process (129); no job is minted. Stdin is held open so the read really
    // blocks; the delay lets the child reach it (an earlier signal ends the process the same way, so a slow start can only weaken
    // this case, never fail it).
    [Theory]
    [InlineData("run")]
    [InlineData("coordinate")]
    public async Task ASighupWhileTheBriefIsBlockedOnStdinEndsTheProcessAtOnceAndMintsNoJobAsync(string verb)
    {
        RequirePosixSignals();
        scenario.Adopt(verb == "run" ? new IsolatedRepo() : IsolatedRepo.ForCoordinate());
        string[] args = verb == "run" ? ["run", "builder", "--brief-file", "-", "--json"] : ["coordinate", "--brief-file", "-", "--json"];
        using RunningCli cli = scenario.Repo.StartCliHoldingStdin(args);
        if (cli.IgnoresSignal(SighupNumber))
            Assert.Skip("this test host was started with SIGHUP ignored, so it cannot reach the child.");

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the child must be waiting on stdin");
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync("HUP", TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(SighupExit, exit);
        Assert.Empty(scenario.Repo.JobDirectories());
        Assert.Equal("", cli.Stdout.Trim());
        Assert.DoesNotContain("cancelling", cli.Stderr, StringComparison.Ordinal);
    }
}
