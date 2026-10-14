using System.Diagnostics;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #88 through the real built binary: a SIGINT (what a terminal's Ctrl-C delivers) reaches Runner, which kills the
// backend's tree and writes a `cancelled` receipt, instead of System.CommandLine's own handler ending the process
// after its 2 s ProcessTerminationTimeout with no result.json and the child still spending (NOTES.md "Ctrl-C reaches
// the runner"). The fake backend writes partial.txt and then sleeps; nothing here reaches a real backend: the child's
// PATH is git's directory plus the system's, and `claustrum.json` committed at the git root points `claude` at the script.
// Each case sleeps a different, odd length so a leftover `sleep` can be told from another case's, and from the machine's.
// The fixture (CancelScenario) is shared with CancelSigtermEndToEndTests, which covers the same ground for #92.
public sealed class CancelSigintEndToEndTests : IDisposable
{
    private const int SigintNumber = 2;

    private readonly CancelScenario scenario = new();

    public void Dispose() => scenario.Dispose();

    private static void RequireSigint()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGINT is a POSIX signal; a Windows console's Ctrl-C cannot be sent to a redirected child from here.");
    }

    private Task<RunningCli> StartAndWaitForPartialAsync(string[] args, bool isolated) =>
        scenario.StartAndWaitForPartialAsync(args, isolated, SigintNumber, "INT");

    private Task AssertCancelledAsync(RunningCli cli, string role, string sleep, bool isolated) =>
        scenario.AssertCancelledAsync(cli, "INT", CancelScenario.CtrlCLine, role, sleep, isolated);

    [Fact]
    public async Task SigintOnAnIsolatedRunEndsItCancelledWithACommitHoldingTheHalfWrittenFileAsync()
    {
        RequireSigint();
        const string sleep = "31.71";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await AssertCancelledAsync(cli, "builder", sleep, isolated: true);
    }

    [Fact]
    public async Task SigintOnAnInPlaceRunEndsItCancelledWithItsReceiptOnDiskAsync()
    {
        RequireSigint();
        const string sleep = "31.72";
        scenario.Prepare(new IsolatedRepo(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: false);

        await AssertCancelledAsync(cli, "builder", sleep, isolated: false);

        // In place there is no worktree and no branch: the half-written file is where the backend left it.
        Assert.True(File.Exists(Path.Combine(scenario.Repo.Repo, "partial.txt")));
        Assert.Equal("main", scenario.Repo.Git("branch", "--show-current"));
    }

    [Fact]
    public async Task SigintOnCoordinateEndsTheArchitectCancelledWithACommitOnItsOwnBranchAsync()
    {
        RequireSigint();
        const string sleep = "31.73";
        scenario.Prepare(IsolatedRepo.ForCoordinate(), sleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["coordinate", "--brief", "do the task", "--json"], isolated: true);

        await AssertCancelledAsync(cli, "architect", sleep, isolated: true);
    }

    // The escape hatch: with ProcessTerminationTimeout null nothing else ends a cancel path that hangs, and the post-run
    // commit runs the repository's hooks. A pre-commit hook that sleeps is the cheapest slow cancel path; the second SIGINT
    // must end the process at once, with no receipt (the job stays `pending`, as it would after any hard kill).
    [Fact]
    public async Task ASecondSigintEndsARunWhoseCancelPathHangsWithoutAReceiptAsync()
    {
        RequireSigint();
        const string sleep = "31.74";
        const string hookSleep = "31.75";
        scenario.Prepare(new IsolatedRepo(), sleep).WriteCast(maxParallel: 2);
        scenario.PlantHangingPreCommitHook(hookSleep);
        using RunningCli cli = await StartAndWaitForPartialAsync(["run", "builder", "--brief", "do the task", "--json"], isolated: true);

        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => cli.Stderr.Contains(CancelScenario.CtrlCLine, StringComparison.Ordinal), "no cancelling line", CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(hookSleep, scenario.Repo.Repo).Length > 0, "the pre-commit hook never started", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        Assert.False(cli.HasExited, "the first SIGINT alone must wait for the hanging commit");

        Stopwatch stopwatch = Stopwatch.StartNew();
        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"the forced exit took {stopwatch.ElapsedMilliseconds} ms");
        Assert.NotEqual(0, exit);
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, CancelScenario.CtrlCLine));
        string jobId = Assert.Single(scenario.Repo.JobDirectories());
        Assert.False(File.Exists(Path.Combine(scenario.Repo.JobsRoot, jobId, "result.json")), "a forced exit writes no receipt");
        Assert.Equal("", cli.Stdout.Trim());
    }
}
