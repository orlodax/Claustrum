using System.Runtime.Versioning;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #106 and #104 through `jobs clean`: the sweep deletes the `.trash-*` a free left and says so, reports what it cannot delete
// as a warning without failing the command, never treats a trash directory as a job, refuses a `.claustrum` that leaves the repo
// before touching anything, and ends a cancelled sweep with exit 130. POSIX shells for the fake git of the cancel case.
public sealed class JobsCleanTrashEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-clean-outside-").FullName;
    private readonly List<string> lockedDirectories = [];
    private readonly List<string> sleeps = [];

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in lockedDirectories.Where(Directory.Exists))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (string sleep in sleeps)
        {
            foreach (string pid in CancelScenario.SleepPids(sleep, repo.Repo))
                System.Diagnostics.Process.GetProcessById(int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture)).Kill();
        }

        repo.Dispose();
        TempTree.Delete(outside);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string Worktrees => Directory.CreateDirectory(Path.Combine(repo.Repo, ".claustrum", "worktrees")).FullName;

    private string MakeTrash(string name)
    {
        string trash = Directory.CreateDirectory(Path.Combine(Worktrees, name)).FullName;
        File.WriteAllText(Path.Combine(trash, "leftover.txt"), "x\n");
        return trash;
    }

    [Fact]
    public async Task LeftoverTrashIsSweptAndCountedExitingZeroAsync()
    {
        string first = MakeTrash(".trash-job-1-aaaaaaaa");
        string second = MakeTrash(".trash-job-2-bbbbbbbb");

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("swept 2 leftover trash directories", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public async Task OneTrashDirectoryIsSingularAsync()
    {
        MakeTrash(".trash-job-1-aaaaaaaa");

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Contains("swept 1 leftover trash directory", Lf(result.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UndeletableTrashIsAWarningLineAndExitZeroAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        string stuck = MakeTrash(".trash-job-1-cccccccc");
        lockedDirectories.Add(stuck);
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(stuck, "probe"), "x");
            Assert.Skip("this account is not stopped by a read-only directory (root, or a capability that bypasses modes).");
        }
        catch (UnauthorizedAccessException)
        {
            // Enforced: run the case.
        }

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("warning: could not sweep", Lf(result.Stderr), StringComparison.Ordinal);
        Assert.Contains(".trash-job-1-cccccccc", result.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(stuck));
    }

    // A directory git lists as a worktree is somebody's whatever it is named, and a finished "job" of that name is not freed.
    [Fact]
    public async Task ARegisteredTrashNamedWorktreeIsNeverTreatedAsAJobAsync()
    {
        string registered = Path.Combine(Worktrees, ".trash-x");
        repo.Git("worktree", "add", "-q", "-b", "somebodys", registered);
        Directory.CreateDirectory(Path.Combine(repo.JobsRoot, ".trash-x"));
        File.WriteAllText(Path.Combine(repo.JobsRoot, ".trash-x", "result.json"), "{}");

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("git lists it as a worktree", result.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(registered, "seed.txt")));
        Assert.Contains(".trash-x", TestGit.WorktreePaths(repo.Repo).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AClaustrumDirectoryLinkedOutIsExitTwoAndNothingOutsideIsTouchedAsync()
    {
        string trash = Directory.CreateDirectory(Path.Combine(outside, "worktrees", ".trash-job-1-dddddddd")).FullName;
        File.WriteAllText(Path.Combine(trash, "keep.txt"), "x\n");
        Directory.CreateDirectory(Path.Combine(outside, "worktrees", "20260101-000000-abcd"));
        TestLinks.CreateDirectoryLink(Path.Combine(repo.Repo, ".claustrum"), outside);
        string[] before = [.. Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("resolves outside the working directory", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("`jobs clean` touches nothing through one", result.Stderr, StringComparison.Ordinal);
        string[] after = [.. Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        Assert.Equal(before, after);
        Assert.True(File.Exists(Path.Combine(trash, "keep.txt")));
    }

    // A fake git that sits in `worktree list` (the sweep's first call) until the signal arrives; every other call is the real git.
    [UnsupportedOSPlatform("windows")]
    private async Task<RunningCli> StartSweepHeldInGitAsync(string sleep, int signalNumber, string signalName, bool ownSession = false)
    {
        sleeps.Add(sleep);
        string bin = Directory.CreateDirectory(Path.Combine(repo.Home, "slow-git")).FullName;
        string real = Path.Combine(IsolatedRepo.ChildPath().Split(Path.PathSeparator)[0], "git");
        string script = Path.Combine(bin, "git");
        File.WriteAllText(script, $"#!/bin/sh\ncase \"$1 $2\" in\n  'worktree list') sleep {sleep} ;;\n  *) exec '{real}' \"$@\" ;;\nesac\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string binary = Path.Combine(AppContext.BaseDirectory, "claustrum");
        RunningCli cli = new(binary, repo.Repo, repo.Home, bin + Path.PathSeparator + IsolatedRepo.ChildPath(), ["jobs", "clean"], newSession: ownSession);
        if (cli.IgnoresSignal(signalNumber))
        {
            cli.Kill();
            cli.Dispose();
            Assert.Skip($"this test host was started with SIG{signalName} ignored, so no SIG{signalName} can reach the child.");
        }

        await cli.WaitUntilAsync(() => CancelScenario.SleepPids(sleep, repo.Repo).Length > 0, "the sweep never reached git", CancelScenario.StartLimit, TestContext.Current.CancellationToken);
        return cli;
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task ACancelDuringTheSweepEndsWithExit130AndLeavesTheTrashAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("SIGINT is a POSIX signal; the fake git is sh.");

        string trash = MakeTrash(".trash-job-1-eeeeeeee");
        using RunningCli cli = await StartSweepHeldInGitAsync("31.95", 2, "INT");

        await cli.SendSigintAsync(TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Cancelled, exit);
        Assert.True(Directory.Exists(trash));
    }

    // #113: `jobs clean` writes no receipt, so the second-signal hint says what a forced exit abandons, not "without a receipt";
    // #111: a closed terminal's SIGHUP cancels the sweep like the other two. The signal goes to the whole process group, as a
    // terminal's does: the fake git's sleep dies with claustrum, and the sweep must still read that as a cancel (exit 130), not as
    // a free that failed.
    [Theory]
    [InlineData("INT", 2, "32.01", "cancelling… press Ctrl-C again to stop now; a free in flight is left for the next jobs clean")]
    [InlineData("TERM", 15, "32.02", "cancelling… send SIGTERM again to stop now; a free in flight is left for the next jobs clean")]
    [InlineData("HUP", 1, "32.03", "cancelling… send SIGTERM or press Ctrl-C again to stop now; a free in flight is left for the next jobs clean")]
    [UnsupportedOSPlatform("windows")]
    public async Task TheFirstGroupSignalDuringTheSweepPrintsTheStopNowHintAndExits130Async(string signal, int number, string sleep, string line)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("these are POSIX signals; the fake git is sh.");

        string trash = MakeTrash(".trash-job-1-ffffffff");
        using RunningCli cli = await StartSweepHeldInGitAsync(sleep, number, signal, ownSession: true);

        await cli.SendGroupSignalAsync(signal, TestContext.Current.CancellationToken);
        int exit = await cli.WaitForExitAsync(CancelScenario.ExitLimit, TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Cancelled, exit);
        Assert.Equal(1, CancelScenario.Count(cli.Stderr, line));
        Assert.DoesNotContain("without a receipt", cli.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("could not remove", cli.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(trash));
    }

    // ---- round 2 (review 2026-10-14) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABranchJobWithoutAReceiptBranchIsFreedAndTheOutputNamesTheBranchItKeptAsync()
    {
        repo.Git("branch", "feature/x");
        string worktree = Path.Combine(Worktrees, "20261014-000000-branchjob");
        repo.Git("worktree", "add", "-q", worktree, "feature/x");
        Directory.CreateDirectory(Path.Combine(repo.JobsRoot, "20261014-000000-branchjob"));
        File.WriteAllText(Path.Combine(repo.JobsRoot, "20261014-000000-branchjob", "result.json"), "{}");

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("removed 20261014-000000-branchjob (branch feature/x kept)", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktree));
        Assert.Contains("feature/x", TestGit.Branches(repo.Repo));
    }

    [Fact]
    public async Task AnUnmountedOperatorWorktreeKeepsItsEntryThroughAJobsCleanAsync()
    {
        string finished = Path.Combine(Worktrees, "20261014-000000-donejob");
        repo.Git("worktree", "add", "-q", "-b", "claustrum/20261014-000000-donejob", finished);
        Directory.CreateDirectory(Path.Combine(repo.JobsRoot, "20261014-000000-donejob"));
        File.WriteAllText(Path.Combine(repo.JobsRoot, "20261014-000000-donejob", "result.json"), "{}");
        string unmounted = Path.Combine(Directory.CreateTempSubdirectory("claustrum-unmounted-").FullName, "drive");
        repo.Git("worktree", "add", "-q", "--detach", unmounted);
        File.WriteAllText(Path.Combine(unmounted, "staged.txt"), "staged\n");
        TestGit.Run(unmounted, "add", "staged.txt");
        string admin = TestGit.Run(unmounted, "rev-parse", "--path-format=absolute", "--git-dir");
        TempTree.Delete(Path.GetDirectoryName(unmounted) ?? unmounted);

        CliResult result = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.False(Directory.Exists(finished));
        Assert.True(File.Exists(Path.Combine(admin, "index")), "the unmounted worktree's entry and staged index must survive");
        Assert.Contains("drive", TestGit.WorktreePaths(repo.Repo).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ATrashLeftByAKilledFreeIsSweptWithItsRegistrationWhenCleanIsGivenALinkedCwdAsync()
    {
        string link = Path.Combine(Directory.CreateTempSubdirectory("claustrum-clean-link-").FullName, "repo-link");
        TestLinks.CreateDirectoryLink(link, repo.Repo);
        string worktree = Path.Combine(Worktrees, "20261014-000000-killedfree");
        repo.Git("worktree", "add", "-q", "-b", "claustrum/20261014-000000-killedfree", worktree);
        string admin = TestGit.Run(worktree, "rev-parse", "--path-format=absolute", "--git-dir");
        string trash = Path.Combine(Worktrees, ".trash-20261014-000000-killedfree-12345678");
        Directory.Move(worktree, trash);

        CliResult result = await repo.RunAsync("jobs", "clean", "--cwd", link);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("swept 1 leftover trash directory", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(trash));
        Assert.False(Directory.Exists(admin));
        Assert.DoesNotContain("killedfree", string.Join('\n', TestGit.WorktreePaths(repo.Repo)), StringComparison.Ordinal);
    }
}
