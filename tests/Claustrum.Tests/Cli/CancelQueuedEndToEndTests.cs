using System.Diagnostics;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Jobs;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #94 through the real built binary: a SIGTERM or SIGINT that lands while a `run` is still queued (for a `max_parallel` slot,
// in place at a cap of 1 or isolated; for a `--branch` lock) or while `git worktree add` runs used to leave the job directory
// `pending` forever and `--json` printing nothing. It is now a `cancelled` receipt, on stdout and in result.json, exit 130, no
// worktree and no `claustrum/<job>` branch, and the runs that were not queued still end cancelled as before. The recorded error
// is the framework's own `A task was canceled.`. Each case sleeps its own odd length (the series continues from
// CancelSigtermEndToEndTests' 31.84), registered with the fixture so a leftover is killed on Dispose.
public sealed class CancelQueuedEndToEndTests : IDisposable
{
    private const int SigintNumber = 2;
    private const int SigtermNumber = 15;
    private const string FrameworkCancel = "A task was canceled.";

    // The clock only separates a prompt exit from a hang (see CancelSigtermEndToEndTests); the receipts carry the verdict.
    private const int PromptlyMilliseconds = 5000;

    // A queued in-place run mints no job until it is refused, so nothing outside the process shows that it reached the wait:
    // this is how long it is given. The build starts in about 0.1 s; a signal that came before the handler would end the
    // process with 143 and no receipt, which fails the case rather than passing it.
    private static readonly TimeSpan queueSettle = TimeSpan.FromSeconds(3);

    private static readonly string[] runArgs = ["run", "builder", "--brief", "do the task", "--json"];

    private readonly CancelScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequirePosixSignals()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGTERM and SIGINT are POSIX signals; nothing here can send one to a redirected Windows child.");
    }

    private static int NumberOf(string signal) => signal == "TERM" ? SigtermNumber : SigintNumber;

    private static string CancellingLineFor(string signal) => signal == "TERM" ? CancelScenario.SigtermLine : CancelScenario.CtrlCLine;

    private static string Str(JsonElement element, string name) => CancelScenario.Str(element, name);

    private string WorktreesDirectory => Path.Combine(scenario.Repo.Repo, ".claustrum", "worktrees");

    private string[] WorktreeDirectories() =>
        Directory.Exists(WorktreesDirectory) ? [.. Directory.GetDirectories(WorktreesDirectory)] : [];

    private int RunsWithAPartialFile() => WorktreeDirectories().Count(directory => File.Exists(Path.Combine(directory, "partial.txt")));

    private string[] JobBranches() =>
        [.. scenario.Repo.Git("branch", "--list", "claustrum/*", "--format=%(refname:short)").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private async Task<string> JobsListAsync() => (await scenario.Repo.RunAsync("jobs", "list")).Stdout;

    private static void SkipIfSignalIsIgnored(RunningCli cli, int number, string name)
    {
        if (!cli.IgnoresSignal(number))
            return;

        cli.Kill();
        Assert.Skip($"this test host was started with SIG{name} ignored, so no SIG{name} can reach the child.");
    }

    /// <summary>Sends <paramref name="signal"/>, waits for the exit and asserts what a run cancelled before it ran owes: exit 130, the cancelling line once, one `cancelled` document on stdout and the same on disk.</summary>
    private async Task<(string JobId, JsonElement Printed, JsonElement OnDisk)> CancelAsync(RunningCli cli, string signal)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSignalAsync(signal, Ct);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, Ct);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < PromptlyMilliseconds, $"exit took {stopwatch.ElapsedMilliseconds} ms\nstderr: {cli.Stderr}");
        Assert.Equal(ExitCodes.Cancelled, exit);
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, CancellingLineFor(signal)));
        using JsonDocument document = JsonDocument.Parse(cli.Stdout);
        JsonElement printed = document.RootElement.Clone();
        string jobId = Str(printed, "job_id");
        JsonElement onDisk = scenario.Repo.ReadResult(jobId);
        Assert.Equal("cancelled", Str(printed, "status"));
        Assert.Equal("cancelled", Str(onDisk, "status"));
        Assert.Equal("builder", Str(onDisk, "role"));
        return (jobId, printed, onDisk);
    }

    /// <summary>A queued run's receipt: no worktree, no branch, the framework's error, nothing spent.</summary>
    private static void AssertNothingRan(JsonElement printed, JsonElement onDisk)
    {
        foreach (JsonElement receipt in new[] { printed, onDisk })
        {
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("worktree").ValueKind);
            Assert.Equal(JsonValueKind.Null, receipt.GetProperty("branch").ValueKind);
            Assert.Equal(FrameworkCancel, Str(receipt, "error"));
        }
    }

    /// <summary>A run that was running when it was cancelled: its work is committed on its own branch, as for any cancelled isolated run.</summary>
    private async Task AssertRunningRunCancelledAsync(RunningCli cli, string signal)
    {
        (string jobId, JsonElement printed, JsonElement onDisk) = await CancelAsync(cli, signal);
        string commit = Str(onDisk, "commit");
        Assert.Equal(Str(printed, "commit"), commit);
        Assert.Equal($"claustrum/{jobId}", Str(onDisk, "branch"));
        Assert.Equal("partial.txt", scenario.Repo.Git("show", "--name-only", "--format=", commit));
    }

    // The owner's case: the cast allows 2 at once, three are started, and the third is cancelled while it waits for a slot.
    [Theory]
    [InlineData("TERM", "31.85")]
    [InlineData("INT", "31.86")]
    public async Task ASignalWhileAnIsolatedRunWaitsForASlotWritesACancelledReceiptAndLeavesNoWorktreeOrBranchAsync(string signal, string sleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli first = await scenario.StartAndWaitForPartialAsync(runArgs, isolated: true, NumberOf(signal), signal);
        using RunningCli second = scenario.Repo.StartCli(runArgs);
        await second.WaitUntilAsync(() => RunsWithAPartialFile() == 2, "the second run never reached its backend", CancelScenario.StartLimit, Ct);
        using RunningCli third = scenario.Repo.StartCli(runArgs);
        await third.WaitUntilAsync(() => scenario.Repo.JobDirectories().Length == 3, "the queued run never minted its job", CancelScenario.StartLimit, Ct);
        Assert.Equal(2, WorktreeDirectories().Length);
        Assert.Equal(2, JobBranches().Length);

        (string jobId, JsonElement printed, JsonElement onDisk) = await CancelAsync(third, signal);

        AssertNothingRan(printed, onDisk);
        Assert.Equal(2, WorktreeDirectories().Length);
        Assert.False(Directory.Exists(scenario.Repo.WorktreePath(jobId)));
        Assert.DoesNotContain($"claustrum/{jobId}", JobBranches());
        string listed = await JobsListAsync();
        Assert.Contains(listed.Split('\n'), line => line.StartsWith(jobId, StringComparison.Ordinal) && line.Contains("cancelled", StringComparison.Ordinal));
        Assert.Equal(2, CancelScenario.Count(listed, "pending"));

        // Must still work: the two runs that held the slots end cancelled, each with a commit holding its half-written file.
        await AssertRunningRunCancelledAsync(first, signal);
        await AssertRunningRunCancelledAsync(second, signal);
        Assert.DoesNotContain("pending", await JobsListAsync(), StringComparison.Ordinal);
        Assert.Equal(2, JobBranches().Length);
        Assert.Empty(CancelScenario.SleepPids(sleep, scenario.Repo.Repo));
    }

    // In place at a cap of 1 the queued run has no job until it is refused, so the receipt is minted for the cancel itself.
    [Fact]
    public async Task ASigtermWhileAnInPlaceRunWaitsForTheCapOfOneWritesACancelledReceiptAsync()
    {
        RequirePosixSignals();
        const string sleep = "31.87";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 1);
        using RunningCli first = await scenario.StartAndWaitForPartialAsync(runArgs, isolated: false, SigtermNumber, "TERM");
        string firstJob = Assert.Single(scenario.Repo.JobDirectories());
        using RunningCli second = scenario.Repo.StartCli(runArgs);
        await Task.Delay(queueSettle, Ct);
        Assert.False(second.HasExited, $"the queued run must still be waiting\nstdout: {second.Stdout}\nstderr: {second.Stderr}");

        (string jobId, JsonElement printed, JsonElement onDisk) = await CancelAsync(second, "TERM");

        AssertNothingRan(printed, onDisk);
        Assert.NotEqual(firstJob, jobId);
        Assert.Equal(2, scenario.Repo.JobDirectories().Length);
        Assert.False(Directory.Exists(WorktreesDirectory));
        Assert.Empty(JobBranches());
        Assert.Equal("main", scenario.Repo.Git("branch", "--show-current"));
        Assert.Equal(1, CancelScenario.Count(await JobsListAsync(), "pending"));

        // Must still work: the run that held the slot is cancelled as before, with its half-written file where it left it.
        (string heldJob, _, JsonElement held) = await CancelAsync(first, "TERM");
        Assert.Equal(firstJob, heldJob);
        Assert.Equal("cancelled", Str(held, "status"));
        Assert.True(File.Exists(Path.Combine(scenario.Repo.Repo, "partial.txt")));
        Assert.DoesNotContain("pending", await JobsListAsync(), StringComparison.Ordinal);
        Assert.Empty(CancelScenario.SleepPids(sleep, scenario.Repo.Repo));
    }

    // `--branch` takes a slot, then the branch's lock: a run that waits on the lock holds a slot, which its cancel must give back.
    [Fact]
    public async Task ASigtermWhileARunWaitsForTheBranchLockWritesACancelledReceiptAndGivesItsSlotBackAsync()
    {
        RequirePosixSignals();
        const string sleep = "31.92";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.Repo.Git("branch", "feature");
        string[] branchArgs = [.. runArgs, "--branch", "feature"];
        using RunningCli first = await scenario.StartAndWaitForPartialAsync(branchArgs, isolated: true, SigtermNumber, "TERM");
        using RunningCli second = scenario.Repo.StartCli(branchArgs);
        await second.WaitUntilAsync(() => scenario.Repo.JobDirectories().Length == 2, "the queued run never minted its job", CancelScenario.StartLimit, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(second.HasExited, $"the queued run must still be waiting\nstdout: {second.Stdout}\nstderr: {second.Stderr}");

        (string jobId, JsonElement printed, JsonElement onDisk) = await CancelAsync(second, "TERM");

        AssertNothingRan(printed, onDisk);
        Assert.False(Directory.Exists(scenario.Repo.WorktreePath(jobId)));
        Assert.Single(WorktreeDirectories());
        Assert.Equal("feature", scenario.Repo.Git("branch", "--list", "feature", "--format=%(refname:short)"));

        // Two slots, one held by the first run: the cancelled run's slot is free again, or this wait for it times out.
        await using (await RoleConcurrencyGate.AcquireAsync(scenario.Repo.Repo, RoleConcurrencyGate.KeyFor("default", "builder"), 2, TimeSpan.FromSeconds(5), Ct))
            Assert.Equal(1, CancelScenario.Count(await JobsListAsync(), "pending"));

        (_, _, JsonElement held) = await CancelAsync(first, "TERM");
        Assert.Equal("cancelled", Str(held, "status"));
        Assert.DoesNotContain("pending", await JobsListAsync(), StringComparison.Ordinal);
    }

    // The add is held open by a post-checkout hook that sleeps (git runs it inside `worktree add`). A cancel there is undone by
    // the add itself (T1); the run's receipt says cancelled, and what the undo removed is gone: the directory, the registration
    // and, for a job's own branch, the branch; a `--branch` run's existing branch is the user's and stays.
    [Theory]
    [InlineData(false, "31.88", "31.89")]
    [InlineData(true, "31.90", "31.91")]
    public async Task ASigtermWhileGitAddsTheWorktreeWritesACancelledReceiptAndLeavesNothingBehindAsync(bool existingBranch, string sleep, string hookSleep)
    {
        RequirePosixSignals();
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        string tip = scenario.Repo.Git("rev-parse", "HEAD");
        if (existingBranch)
            scenario.Repo.Git("branch", "feature");

        scenario.PlantHangingHook("post-checkout", hookSleep);
        using RunningCli cli = scenario.Repo.StartCli(existingBranch ? [.. runArgs, "--branch", "feature"] : runArgs);
        SkipIfSignalIsIgnored(cli, SigtermNumber, "TERM");
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the post-checkout hook never started", CancelScenario.StartLimit, Ct);

        (string jobId, JsonElement printed, JsonElement onDisk) = await CancelAsync(cli, "TERM");

        AssertNothingRan(printed, onDisk);
        Assert.Empty(WorktreeDirectories());
        Assert.False(Directory.Exists(scenario.Repo.WorktreePath(jobId)));
        Assert.Empty(JobBranches());
        Assert.Single(TestGit.WorktreePaths(scenario.Repo.Repo));
        if (existingBranch)
            Assert.Equal(tip, scenario.Repo.Git("rev-parse", "feature"));

        Assert.Single(scenario.Repo.JobDirectories());
        Assert.DoesNotContain("pending", await JobsListAsync(), StringComparison.Ordinal);
        Assert.Empty(CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo));
    }
}
