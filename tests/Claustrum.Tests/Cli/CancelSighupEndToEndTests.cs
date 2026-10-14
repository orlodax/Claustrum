using System.Diagnostics;
using System.Text.Json;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #111 through the real built binary, after the review of 2026-10-14 that found the first design failed its main scenario: a
// closing terminal signals the whole foreground PROCESS GROUP, twice within 0.3 ms, and the backend dies with claustrum. So
// every case here starts the child as the leader of a session of its own and signals `-<pgid>` (RunningCli.SendGroupSignalAsync);
// a single-pid signal never reaches that. Expected: the first signal of any kind cancels (a backend killed by the group signal
// counts as cancelled, `failed` never), the receipt is written, exit 130, the stderr line once; SIGHUP never forces the exit, any
// number of them; a later SIGTERM or Ctrl-C after a SIGHUP does. A hang after the first signal needs a git that ignores the group
// signal (CancelScenario.PlantSignalImmuneGit), as it would not die in a real terminal either only if it were stuck.
// Each case sleeps its own odd length (32.xx). POSIX only; the pty cases are in CancelPtyEndToEndTests.
public sealed class CancelSighupEndToEndTests : IDisposable
{
    private const int SighupNumber = 1;
    private const int SigintNumber = 2;
    private const int SigtermNumber = 15;
    private const int SighupExit = 129;

    // Only separates a prompt exit from a hang (the receipts and exit codes carry the verdict); the grace for a signalled backend is 1 s.
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

    private Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated, string signal = "HUP") =>
        scenario.StartAndWaitForPartialAsync(args, isolated, NumberOf(signal), signal, ownSession: true);

    [Theory]
    [InlineData("HUP", "32.11")]
    [InlineData("INT", "32.12")]
    public async Task AGroupSignalOnAnIsolatedRunEndsItCancelledWithACommitHoldingTheHalfWrittenFileAsync(string signal, string sleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, signal);

        await scenario.AssertCancelledAsync(cli, signal, LineOf(signal), "builder", sleep, isolated: true, PromptlyMilliseconds, group: true);
    }

    [Fact]
    public async Task AGroupSighupOnAnInPlaceRunEndsItCancelledAndKeepsTheHalfWrittenFileAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.13";
        scenario.Prepare(new IsolatedRepo(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: false);

        await scenario.AssertCancelledAsync(cli, "HUP", CancelScenario.SighupLine, "builder", sleep, isolated: false, PromptlyMilliseconds, group: true);

        Assert.True(File.Exists(Path.Combine(scenario.Repo.Repo, "partial.txt")));
    }

    [Theory]
    [InlineData("HUP", "32.14")]
    [InlineData("INT", "32.15")]
    public async Task AGroupSignalOnCoordinateEndsTheArchitectCancelledWithACommitOnItsOwnBranchAsync(string signal, string sleep)
    {
        RequirePosixSignals();
        scenario.Prepare(IsolatedRepo.ForCoordinate(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["coordinate", "--brief", "do the task", "--json"], isolated: true, signal);

        await scenario.AssertCancelledAsync(cli, signal, LineOf(signal), "architect", sleep, isolated: true, PromptlyMilliseconds, group: true);
    }

    // A closing terminal's two SIGHUPs, 0.3 ms apart (measured): the second killed the run before its receipt when it forced the exit.
    [Fact]
    public async Task TwoSighupsSentBackToBackToTheGroupStillEndTheRunCancelledWithItsReceiptAsync()
    {
        RequirePosixSignals();
        const string sleep = "32.16";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await scenario.AssertCancelledAsync(cli, "HUP", CancelScenario.SighupLine, "builder", sleep, isolated: true, PromptlyMilliseconds, group: true, times: 2);
    }

    // After the first signal the cancel path hangs in the repository's pre-commit hook (git and the hook ignore the group signal,
    // so they can). Then: a later Ctrl-C or SIGTERM ends the process at once with no receipt, however the first one went; a
    // later SIGHUP, of any number, never does — the run waits for the hook and then writes its cancelled receipt.
    [Theory]
    [InlineData("HUP", "INT", true, "32.17", "32.18")]
    [InlineData("HUP", "TERM", true, "32.19", "32.20")]
    [InlineData("INT", "HUP", false, "32.21", "32.22")]
    [InlineData("TERM", "HUP", false, "32.23", "32.24")]
    [InlineData("HUP", "HUP", false, "32.25", "32.26")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ALaterGroupSignalForcesTheExitExactlyWhenItIsNotASighupAsync(string first, string second, bool forces, string sleep, string hookSleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantSignalImmuneGit();
        scenario.PlantHangingPreCommitHook(hookSleep);
        string firstLine = LineOf(first);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true, first);
        if (cli.IgnoresSignal(NumberOf(second)))
            Assert.Skip($"this test host was started with SIG{second} ignored, so it cannot reach the child.");

        await cli.SendGroupSignalAsync(first, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(firstLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, $"the first SIG{first} alone must wait for the hanging commit");

        await cli.SendGroupSignalAsync(second, TestContext.Current.CancellationToken);
        if (forces)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
            Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
            string jobId = Assert.Single(scenario.Repo.JobDirectories());
            Assert.False(File.Exists(Path.Combine(scenario.Repo.JobsRoot, jobId, "result.json")), "a forced exit writes no receipt");
            Assert.Equal("", cli.Stdout.Trim());
            return;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, $"a SIG{second} after a SIG{first} must not force the exit\nstdout: {cli.Stdout}\nstderr: {cli.Stderr}");

        scenario.KillSleeps(hookSleep);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(130, exit);
        (_, JsonElement onDisk) = scenario.OnlyReceipt();
        Assert.Equal("cancelled", CancelScenario.Str(onDisk, "status"));
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, firstLine));
    }

    // The brief is resolved before the handler exists (RunCommand/CoordinateCommand), so a SIGHUP while a stdin read blocks finds
    // nothing listening and the runtime's default ends the process (129); no job is minted. Stdin is held open so the read really
    // blocks; the delay lets the child reach it (an earlier signal ends the process the same way, so a slow start can only weaken
    // this case, never fail it).
    [Theory]
    [InlineData("run")]
    [InlineData("coordinate")]
    public async Task AGroupSighupWhileTheBriefIsBlockedOnStdinEndsTheProcessAtOnceAndMintsNoJobAsync(string verb)
    {
        RequirePosixSignals();
        scenario.Adopt(verb == "run" ? new IsolatedRepo() : IsolatedRepo.ForCoordinate());
        string[] args = verb == "run" ? ["run", "builder", "--brief-file", "-", "--json"] : ["coordinate", "--brief-file", "-", "--json"];
        using RunningCli cli = scenario.Repo.StartCliHoldingStdinInOwnSession(args);
        if (cli.IgnoresSignal(SighupNumber))
            Assert.Skip("this test host was started with SIGHUP ignored, so it cannot reach the child.");

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the child must be waiting on stdin");
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendGroupSignalAsync("HUP", TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"the exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(SighupExit, exit);
        Assert.Empty(scenario.Repo.JobDirectories());
        Assert.Equal("", cli.Stdout.Trim());
        Assert.DoesNotContain("cancelling", cli.Stderr, StringComparison.Ordinal);
    }

    // The signal lands while `git worktree add` runs its slow post-checkout hook: git dies of it with 128 + the signal, and that is a
    // cancel, not git saying no. Nothing is left: no job directory under .claustrum/worktrees, no claustrum/<job> branch; a branch
    // that was given with --branch is the operator's and stays.
    [Theory]
    [InlineData("HUP", false, "32.27")]
    [InlineData("INT", false, "32.28")]
    [InlineData("HUP", true, "32.29")]
    [InlineData("INT", true, "32.30")]
    public async Task AGroupSignalDuringWorktreeAddCancelsLeavesNothingAndKeepsAGivenBranchAsync(string signal, bool givenBranch, string hookSleep)
    {
        RequirePosixSignals();
        scenario.Adopt(new IsolatedRepo()).WriteCast(maxParallel: 2);
        scenario.Repo.Git("branch", "feature/x");
        scenario.PlantHangingHook("post-checkout", hookSleep, ignoreSignals: false);
        string[] args = givenBranch
            ? ["run", "builder", "--brief", "do the task", "--json", "--branch", "feature/x"]
            : ["run", "builder", "--brief", "do the task", "--json"];
        using RunningCli cli = scenario.Repo.StartCliInOwnSession(args);
        if (cli.IgnoresSignal(NumberOf(signal)))
            Assert.Skip($"this test host was started with SIG{signal} ignored, so it cannot reach the child.");

        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the post-checkout hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        await cli.SendGroupSignalAsync(signal, TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(130, exit);
        string worktrees = Path.Combine(scenario.Repo.Repo, ".claustrum", "worktrees");
        Assert.True(!Directory.Exists(worktrees) || Directory.GetFileSystemEntries(worktrees).Length == 0, "no job directory is left");
        Assert.DoesNotContain("claustrum/", scenario.Repo.Git("branch", "--format=%(refname:short)"), StringComparison.Ordinal);
        Assert.Contains("feature/x", scenario.Repo.Git("branch", "--format=%(refname:short)"), StringComparison.Ordinal);
        Assert.DoesNotContain("failed", cli.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // The race the signal-exit mapping exists for, made deterministic: git (the snapshot's `status` of an in-place run, or the
    // `rev-parse --verify HEAD` that starts an isolated one) dies of SIGHUP on its own, and claustrum itself hears the SIGHUP 50 ms
    // later. The run must read git's death as the cancel it is: exit 130 and a cancelled receipt, not a failed one or exit 2/1.
    [Theory]
    [InlineData("status --porcelain", false, "32.31")]
    [InlineData("rev-parse --verify HEAD", true, "32.32")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AGitThatDiesOfSighupJustBeforeClaustrumHearsItIsACancelWithAReceiptAsync(string call, bool isolated, string gitSleep)
    {
        RequirePosixSignals();
        scenario.Adopt(new IsolatedRepo()).WriteCast(maxParallel: 2);
        string pidFile = Path.Combine(scenario.Repo.Home, "git.pid");
        scenario.PlantGitSleepingOn(call, gitSleep, pidFile);
        using RunningCli cli = scenario.Repo.StartCli("run", "builder", "--brief", "do the task", "--json");
        if (cli.IgnoresSignal(SighupNumber))
            Assert.Skip("this test host was started with SIGHUP ignored, so it cannot reach the child.");

        await cli.WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0, "git never reached the call", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        string gitPid = File.ReadAllText(pidFile).Trim();
        await RunShellAsync($"kill -s HUP {gitPid}");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cli.SendSignalAsync("HUP", TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(130, exit);
        (_, JsonElement onDisk) = scenario.OnlyReceipt();
        Assert.Equal("cancelled", CancelScenario.Str(onDisk, "status"));
        string worktrees = Path.Combine(scenario.Repo.Repo, ".claustrum", "worktrees");
        if (isolated)
            Assert.True(!Directory.Exists(worktrees) || Directory.GetFileSystemEntries(worktrees).Length == 0, "no job directory is left");
    }

    private static async Task RunShellAsync(string command)
    {
        ProcessStartInfo startInfo = new() { FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        using Process shell = Process.Start(startInfo) ?? throw new InvalidOperationException("sh failed to start");
        await shell.WaitForExitAsync(TestContext.Current.CancellationToken);
    }
}
