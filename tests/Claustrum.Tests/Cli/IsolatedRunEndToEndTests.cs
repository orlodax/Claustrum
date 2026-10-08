using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// M4 wave 1 through the real built binary, against a real repository and a fake `claude` script
// (IsolatedRepo): an isolated builder's work lands on its branch as a commit (#61), the receipt on disk names
// the branch (#60), `--branch` continues on an existing branch (#63), the brief rides on stdin (#68) and an
// isolated run may not check out another branch (#62). Nothing here can reach a real backend: the child's PATH
// is git's directory plus the system's, and the configured script path is what the claude backend runs.
public sealed class IsolatedRunEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static bool IsNull(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null;

    private string[] WorktreeDirectories()
    {
        string root = Path.Combine(repo.Repo, ".claustrum", "worktrees");
        return Directory.Exists(root) ? [.. Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>()] : [];
    }

    // Cast max_parallel 2, a fake claude that leaves `first.txt` behind: the first isolated job.
    private async Task<JsonElement> FirstIsolatedJobAsync()
    {
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "added first.txt" });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        return result;
    }

    [Fact]
    public async Task AnIsolatedBuildersWorkIsACommitOnItsBranchAndTheReceiptOnDiskNamesItAsync()
    {
        JsonElement result = await FirstIsolatedJobAsync();

        string jobId = Str(result, "job_id");
        string branch = $"claustrum/{jobId}";
        string tip = TestGit.RevParse(repo.Repo, $"refs/heads/{branch}");
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal(branch, Str(result, "branch"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(result, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tip, Str(result, "commit"));
        Assert.Equal(1, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.Equal($"claustrum builder {jobId}\n\nadded first.txt", TestGit.Run(repo.Repo, "log", "-1", "--format=%B", tip));
        Assert.Equal("", TestGit.Status(repo.WorktreePath(jobId)));
        Assert.Equal("first.txt", Assert.Single(result.GetProperty("changed_files").EnumerateArray()).GetProperty("path").GetString());

        // #60: jobs show reads the file; the file is what names the branch.
        JsonElement onDisk = repo.ReadResult(jobId);
        Assert.Equal(branch, Str(onDisk, "branch"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(onDisk, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tip, Str(onDisk, "commit"));
        Assert.Equal("main", TestGit.Run(repo.Repo, "branch", "--show-current"));
    }

    // #63, the brief's "retry once": the finished job's clean worktree is freed, a new one is cut on the same
    // branch (no `-b`), and the new work is committed on top of the reviewed work.
    [Fact]
    public async Task ABranchRunOnAFinishedJobsBranchFreesItsWorktreeAndCommitsOntoTheSameBranchAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string firstId = Str(first, "job_id");
        string branch = Str(first, "branch");
        repo.Script(new FakeClaudeScript { Writes = [("second.txt", "second")], Summary = "added second.txt" });

        (CliResult process, JsonElement second) = await repo.RunBuilderAsync("--branch", branch);

        string secondId = Str(second, "job_id");
        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(branch, Str(second, "branch"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(secondId), Str(second, "worktree"), StringComparison.OrdinalIgnoreCase);
        string tip = TestGit.RevParse(repo.Repo, $"refs/heads/{branch}");
        Assert.Equal(tip, Str(second, "commit"));
        Assert.NotEqual(Str(first, "commit"), tip);
        Assert.Equal(2, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.Equal(Str(first, "commit"), TestGit.RevParse(repo.Repo, $"{tip}~1"));
        Assert.Equal(["first.txt", "second.txt"], [.. TestGit.Run(repo.Repo, "ls-tree", "--name-only", tip).Split('\n').Where(name => name.EndsWith(".txt", StringComparison.Ordinal) && name != "seed.txt")]);
        Assert.False(Directory.Exists(repo.WorktreePath(firstId)));
        Assert.True(Directory.Exists(repo.WorktreePath(secondId)));
        Assert.DoesNotContain($"claustrum/{secondId}", TestGit.Branches(repo.Repo));
        Assert.Equal(branch, TestGit.Run(repo.WorktreePath(secondId), "branch", "--show-current"));
        Assert.Equal(branch, Str(repo.ReadResult(secondId), "branch"));
    }

    // The architect's fan-out under `max_parallel: 3`: three builders at once, each in its own worktree on its
    // own branch, all committing into one object database and one set of refs at the same moment.
    [Fact]
    public async Task ThreeConcurrentIsolatedBuildersEachCommitOnTheirOwnBranchAsync()
    {
        repo.WriteCast(maxParallel: 3);
        repo.Script(new FakeClaudeScript { WriteUniqueFile = true, SleepSeconds = 1, Summary = "wrote a unique file" });

        (CliResult Process, JsonElement Result)[] runs = await Task.WhenAll(repo.RunBuilderAsync(), repo.RunBuilderAsync(), repo.RunBuilderAsync());

        foreach ((CliResult process, JsonElement result) in runs)
        {
            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
            string branch = Str(result, "branch");
            Assert.Equal($"claustrum/{Str(result, "job_id")}", branch);
            Assert.Equal(TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"), Str(result, "commit"));
            Assert.Equal(1, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
            Assert.Equal("", TestGit.Status(Str(result, "worktree")));
        }

        Assert.Equal(3, runs.Select(run => Str(run.Result, "branch")).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, TestGit.WorktreePaths(repo.Repo).Length);
        Assert.Equal("main", TestGit.Run(repo.Repo, "branch", "--show-current"));
        Assert.Equal("", TestGit.Status(repo.Repo));
    }

    // Each refusal is a `status: failed` receipt on stdout with exit 1 — never a stack trace, never exit 2 —
    // and leaves no worktree behind (F11).
    private async Task<JsonElement> AssertRefusedAsync(string branch, string expectedError, int expectedWorktrees = 0)
    {
        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--branch", branch, "--backend", "nonexistent");

        Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
        Assert.Equal("failed", Str(result, "status"));
        Assert.Contains(expectedError, Str(result, "error"), StringComparison.Ordinal);
        Assert.True(IsNull(result, "worktree"));
        Assert.True(IsNull(result, "branch"));
        Assert.True(IsNull(result, "commit"));
        Assert.Equal("failed", Str(repo.ReadResult(Str(result, "job_id")), "status"));
        Assert.Equal(expectedWorktrees, WorktreeDirectories().Length);
        Assert.False(Directory.Exists(repo.WorktreePath(Str(result, "job_id"))));
        return result;
    }

    [Fact]
    public async Task ABranchThatDoesNotExistIsRefusedAsAFailedReceiptAsync()
    {
        string head = TestGit.Head(repo.Repo);

        await AssertRefusedAsync("nosuch", "--branch nosuch: no local branch of that name");

        Assert.Equal(["main"], TestGit.Branches(repo.Repo));
        Assert.Equal(head, TestGit.Head(repo.Repo));
        Assert.Single(TestGit.WorktreePaths(repo.Repo));
    }

    // `main~1` resolves under refs/heads/ for rev-parse and would check out a detached HEAD the run would
    // commit onto no branch (measured, git 2.56); the existence check is show-ref.
    [Fact]
    public async Task ARevisionThatIsNotABranchNameIsRefusedAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, "second.txt"), "two\n");
        TestGit.CommitAll(repo.Repo, "second");

        await AssertRefusedAsync("main~1", "--branch main~1: no local branch of that name");

        Assert.Single(TestGit.WorktreePaths(repo.Repo));
    }

    [Fact]
    public async Task TheBranchCheckedOutInTheMainCheckoutIsRefusedNamingTheCheckoutAsync()
    {
        JsonElement result = await AssertRefusedAsync("main", "--branch main: already checked out in");

        Assert.Contains(Path.GetFileName(repo.Repo), Str(result, "error"), StringComparison.Ordinal);
        Assert.Equal("main", TestGit.Run(repo.Repo, "branch", "--show-current"));
    }

    [Fact]
    public async Task ARunningJobsWorktreeIsRefusedAndLeftIntactAsync()
    {
        string running = "20260101-000000-aaaa1111";
        TestGit.Run(repo.Repo, "worktree", "add", "-q", repo.WorktreePath(running), "-b", $"claustrum/{running}");
        File.WriteAllText(Path.Combine(repo.WorktreePath(running), "in-progress.txt"), "half done\n");
        Directory.CreateDirectory(Path.Combine(repo.JobsRoot, running));

        await AssertRefusedAsync($"claustrum/{running}", "which has not finished (no result.json)", expectedWorktrees: 1);

        Assert.Equal("half done\n", File.ReadAllText(Path.Combine(repo.WorktreePath(running), "in-progress.txt")));
        Assert.False(File.Exists(Path.Combine(repo.JobsRoot, running, "result.json")));
    }

    [Fact]
    public async Task AJobIdThisClaustrumHomeHasNoDirectoryForIsRefusedAsUnknownAsync()
    {
        string foreign = "20260101-000000-bbbb2222";
        TestGit.Run(repo.Repo, "worktree", "add", "-q", repo.WorktreePath(foreign), "-b", $"claustrum/{foreign}");

        JsonElement result = await AssertRefusedAsync($"claustrum/{foreign}", "which is unknown under this CLAUSTRUM_HOME", expectedWorktrees: 1);

        Assert.Contains("claustrum jobs clean --cwd", Str(result, "error"), StringComparison.Ordinal);
        Assert.True(Directory.Exists(repo.WorktreePath(foreign)));
    }

    [Fact]
    public async Task ADirtyFinishedWorktreeIsRefusedAndItsChangesSurviveAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string firstId = Str(first, "job_id");
        File.WriteAllText(Path.Combine(repo.WorktreePath(firstId), "dirty.txt"), "not committed\n");

        await AssertRefusedAsync(Str(first, "branch"), "which was left in place", expectedWorktrees: 1);

        Assert.Equal("not committed\n", File.ReadAllText(Path.Combine(repo.WorktreePath(firstId), "dirty.txt")));
        Assert.Contains(Str(first, "branch"), TestGit.Branches(repo.Repo));
    }

    // A run that fails once its worktree is cut keeps the branch it was given (#63), and the runner still
    // commits whatever the failed run left.
    [Fact]
    public async Task ABranchRunThatFailsAfterTheWorktreeIsCutKeepsTheBranchAndCommitsWhatItLeftAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string branch = Str(first, "branch");
        repo.Script(new FakeClaudeScript { Writes = [("partial.txt", "partial")], Fail = true });

        (CliResult process, JsonElement second) = await repo.RunBuilderAsync("--branch", branch);

        Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
        Assert.Equal("failed", Str(second, "status"));
        Assert.Equal(branch, Str(second, "branch"));
        Assert.Equal(TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"), Str(second, "commit"));
        Assert.Equal(2, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.True(Directory.Exists(repo.WorktreePath(Str(second, "job_id"))));
    }

    [Fact]
    public async Task ABranchRunWhoseBackendIsMissingKeepsTheBranchAndWorktreeAndHasNoCommitAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string branch = Str(first, "branch");
        string tip = TestGit.RevParse(repo.Repo, $"refs/heads/{branch}");

        (CliResult process, JsonElement second) = await repo.RunBuilderAsync("--branch", branch, "--backend", "nonexistent");

        Assert.Equal(ExitCodes.BackendMissing, process.ExitCode);
        Assert.Equal(branch, Str(second, "branch"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(Str(second, "job_id")), Str(second, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.True(IsNull(second, "commit"));
        Assert.Equal(tip, TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"));
        Assert.True(Directory.Exists(repo.WorktreePath(Str(second, "job_id"))));
    }

    // #63: a `--branch` job's worktree is on the given branch, not claustrum/<its id>, and `jobs clean` names
    // the branch the receipt recorded.
    [Fact]
    public async Task JobsCleanNamesTheBranchTheReceiptRecordedAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string branch = Str(first, "branch");
        repo.Script(new FakeClaudeScript { Writes = [("second.txt", "second")] });
        (_, JsonElement second) = await repo.RunBuilderAsync("--branch", branch);
        string secondId = Str(second, "job_id");

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Contains($"removed {secondId} (branch {branch} kept)", clean.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.WorktreePath(secondId)));
        Assert.Contains(branch, TestGit.Branches(repo.Repo));
    }

    // A receipt written before #60 has no branch, a pruned job has no receipt, and a half-written one is not
    // JSON: `jobs clean` then names the branch the job's id implies, as it always did.
    [Theory]
    [InlineData("no job directory")]
    [InlineData("a receipt without a branch")]
    [InlineData("a receipt that is not JSON")]
    public async Task JobsCleanFallsBackToTheBranchTheJobIdImpliesWhenTheReceiptRecordsNoneAsync(string receipt)
    {
        string jobId = "20260101-000000-cccc3333";
        TestGit.Run(repo.Repo, "worktree", "add", "-q", repo.WorktreePath(jobId), "-b", $"claustrum/{jobId}");
        Directory.CreateDirectory(repo.JobsRoot);
        if (receipt != "no job directory")
        {
            Directory.CreateDirectory(Path.Combine(repo.JobsRoot, jobId));
            File.WriteAllText(Path.Combine(repo.JobsRoot, jobId, "result.json"), receipt == "a receipt without a branch" ? /*lang=json,strict*/ """{"status":"success"}""" : "{ not json");
        }

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Contains($"removed {jobId} (branch claustrum/{jobId} kept)", clean.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.WorktreePath(jobId)));
    }

    // F3: `jobs clean` never forces. A finished worktree that still holds uncommitted work is reported, left,
    // and the sweep exits 1.
    [Fact]
    public async Task JobsCleanLeavesAFinishedWorktreeWithUncommittedWorkInPlaceAndExitsOneAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string firstId = Str(first, "job_id");
        File.WriteAllText(Path.Combine(repo.WorktreePath(firstId), "dirty.txt"), "not committed\n");

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.BackendFailure, clean.ExitCode);
        Assert.Contains($"could not remove {firstId}:", clean.Stderr, StringComparison.Ordinal);
        Assert.Contains("left in place: uncommitted changes:", clean.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("removed", clean.Stdout, StringComparison.Ordinal);
        Assert.Equal("not committed\n", File.ReadAllText(Path.Combine(repo.WorktreePath(firstId), "dirty.txt")));
        Assert.Contains(Str(first, "branch"), TestGit.Branches(repo.Repo));
    }

    // R1: a lock per branch, held from before the finished worktree is freed until the result is written.
    // Held here by the test itself; the child process waits its --timeout and comes back with a receipt.
    [Fact]
    public async Task ABranchRunWaitsForTheBranchLockThenIsRefusedWhenItOutlastsTheWaitAsync()
    {
        JsonElement first = await FirstIsolatedJobAsync();
        string firstId = Str(first, "job_id");
        string branch = Str(first, "branch");
        string slotFile = Path.Combine(repo.Repo, ".claustrum", "locks", $"{RoleConcurrencyGate.KeyFor("default", "builder")}.0.lock");

        await using (await RoleConcurrencyGate.AcquireAsync(repo.Repo, JobWorktree.LockKeyFor(branch), 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
        {
            (CliResult process, JsonElement refused) = await repo.RunBuilderAsync("--branch", branch, "--timeout", "1");

            Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
            Assert.Equal("failed", Str(refused, "status"));
            Assert.Contains($"--branch {branch}: another run on this branch outlasted the wait", Str(refused, "error"), StringComparison.Ordinal);
            Assert.True(IsNull(refused, "worktree"));
            Assert.Equal([firstId], WorktreeDirectories());
            Assert.Equal("", TestGit.Status(repo.WorktreePath(firstId)));
        }

        // The cast slot the refused run held while it waited is free again (the lock file is never deleted,
        // only closed), and with the branch lock released the same request goes through.
        using (FileStream slot = new(slotFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.NotNull(slot);

        repo.Script(new FakeClaudeScript { Writes = [("after.txt", "after")] });
        (CliResult retried, JsonElement second) = await repo.RunBuilderAsync("--branch", branch);
        Assert.True(retried.ExitCode == ExitCodes.Ok, retried.Stderr);
        Assert.Equal(branch, Str(second, "branch"));
    }

    // Two `--branch` runs on one branch started together serialise: git takes no lock between its
    // "checked out elsewhere?" check and the add, and two worktrees on one branch revert each other's work
    // (measured by the reviewer: 14 of 30 racing adds succeeded). The markers are the script's own start/end.
    [Fact]
    public async Task TwoConcurrentBranchRunsSerialiseAndBothCommitsLandOnTheBranchAsync()
    {
        repo.WriteCast(maxParallel: 3);
        repo.Script(new FakeClaudeScript { WriteUniqueFile = true });
        (_, JsonElement first) = await repo.RunBuilderAsync();
        string branch = Str(first, "branch");
        repo.Script(new FakeClaudeScript
        {
            WriteUniqueFile = true,
            SleepSeconds = 1,
            MarkerLog = repo.MarkerLog,
            StartStampDirectory = OperatingSystem.IsWindows() ? null : repo.StampDirectory,
        });

        Task<(CliResult Process, JsonElement Result)> a = repo.RunBuilderAsync("--branch", branch);
        Task<(CliResult Process, JsonElement Result)> b = repo.RunBuilderAsync("--branch", branch);
        (CliResult Process, JsonElement Result)[] both = await Task.WhenAll(a, b);

        foreach ((CliResult process, JsonElement result) in both)
        {
            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
            Assert.Equal(branch, Str(result, "branch"));
        }

        Assert.Equal(["start", "end", "start", "end"], File.ReadAllLines(repo.MarkerLog));

        // "The second starts after the first's result.json": the lock is held until the result is written,
        // not merely until the role's process exits. Whichever run finished first, its result.json is on disk
        // before the other run's role began.
        if (!OperatingSystem.IsWindows())
        {
            DateTime[] results = [.. both.Select(run => File.GetLastWriteTimeUtc(Path.Combine(repo.JobsRoot, Str(run.Result, "job_id"), "result.json"))).Order()];
            DateTime[] starts = [.. Directory.GetFiles(repo.StampDirectory).Select(File.GetLastWriteTimeUtc).Order()];
            Assert.Equal(2, starts.Length);
            Assert.True(starts[1] >= results[0], $"the second run's role started at {starts[1]:O}, before the first result.json at {results[0]:O}");
        }

        string tip = TestGit.RevParse(repo.Repo, $"refs/heads/{branch}");
        string[] commits = [.. both.Select(run => Str(run.Result, "commit"))];
        Assert.Equal(2, commits.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(tip, commits);
        Assert.Equal(3, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.Equal(3, TestGit.Run(repo.Repo, "ls-tree", "--name-only", tip).Split('\n').Count(name => name.StartsWith("out-", StringComparison.Ordinal)));
        bool firstIsOlder = TestGit.Try(repo.Repo, "merge-base", "--is-ancestor", commits[0], commits[1]).ExitCode == 0;
        bool secondIsOlder = TestGit.Try(repo.Repo, "merge-base", "--is-ancestor", commits[1], commits[0]).ExitCode == 0;
        Assert.True(firstIsOlder ^ secondIsOlder, "one run's commit must sit on top of the other's, not beside it");
    }
}
