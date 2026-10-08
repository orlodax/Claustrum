using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #63: `run --branch` puts a new job worktree on an existing local branch, freeing a finished job's clean
// worktree first and refusing everything else with a BranchRefusedException (a receipt, never exit 2).
// Every case runs against a real repository and real `git worktree`; the "jobs root" is a scratch directory
// standing in for `$CLAUSTRUM_HOME/jobs`, where a job counts as finished once its result.json exists (R3).
public sealed class JobWorktreeBranchTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Finish(string jobsRoot, string jobId)
    {
        Directory.CreateDirectory(Path.Combine(jobsRoot, jobId));
        File.WriteAllText(Path.Combine(jobsRoot, jobId, "result.json"), "{}");
    }

    // git lists a worktree by its realpath, which need not equal the path as typed (a symlinked cwd, a Windows
    // 8.3 %TEMP%), so a registration is looked for by its directory name, which these tests make unique.
    private static string[] RegisteredNames(string repo) => [.. GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path))];

    private static void Commit(string worktree, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(worktree, fileName), content);
        GitRepo.Run(worktree, "add", "-A");
        GitRepo.Run(worktree, "commit", "-q", "-m", $"add {fileName}");
    }

    [Fact]
    public async Task AFinishedJobsCleanWorktreeIsFreedAndItsBranchCheckedOutInANewOneAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Commit(first.Path, "work.txt", "first\n");
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Finish(jobs, "job-1");

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(first.Path));
        Assert.Equal(JobWorktree.PathFor(repo, "job-2"), second.Path);
        Assert.Equal("claustrum/job-1", second.Branch);
        Assert.Equal(repo, second.MainCheckout);
        Assert.Equal(tip, second.BaseCommit);
        Assert.False(second.OwnsBranch);
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(second.Path, "work.txt")));
        Assert.Equal("claustrum/job-1", GitRepo.Run(second.Path, "branch", "--show-current"));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // A branch nothing holds is just added; there is nothing to free.
    [Fact]
    public async Task AFreeBranchGetsAWorktreeWithoutFreeingAnythingAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        GitRepo.Run(repo, "branch", "feature/free");

        JobWorktreeInfo info = await JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/free", jobs, Ct);

        Assert.Equal("feature/free", GitRepo.Run(info.Path, "branch", "--show-current"));
        Assert.Equal(GitRepo.Head(repo), info.BaseCommit);
    }

    [Theory]
    [InlineData("nosuch")]
    [InlineData("main~1")]
    [InlineData("HEAD")]
    [InlineData("refs/heads/main")]
    public async Task ANameThatIsNotALocalBranchIsRefusedAndNothingIsCreatedAsync(string name)
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Commit(repo, "second.txt", "two\n");
        string jobs = scratch.CreateDirectory();

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-1", name, jobs, Ct));

        Assert.Contains($"--branch {name}: no local branch of that name", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.Single(GitRepo.WorktreePaths(repo));
    }

    // git's own DWIM would cut a local branch from a same-named remote-tracking one. A run that asked for an
    // existing branch must not quietly create one.
    [Fact]
    public async Task ARemoteTrackingBranchIsNotMadeIntoALocalOneAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "update-ref", "refs/remotes/origin/feat", GitRepo.Head(repo));
        string jobs = scratch.CreateDirectory();

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feat", jobs, Ct));

        Assert.Contains("no local branch of that name", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("feat", GitRepo.Branches(repo));
        Assert.False(await JobWorktree.BranchExistsAsync(repo, "feat", Ct));
    }

    // Measured on git 2.56: `rev-parse --verify refs/heads/main~1` succeeds, so existence is `show-ref`.
    [Theory]
    [InlineData("main", true)]
    [InlineData("main~1", false)]
    [InlineData("main^", false)]
    [InlineData("refs/heads/main", false)]
    [InlineData("nosuch", false)]
    public async Task BranchExistsAsyncTakesExactLocalBranchNamesOnlyAsync(string name, bool expected)
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Commit(repo, "second.txt", "two\n");

        Assert.Equal(expected, await JobWorktree.BranchExistsAsync(repo, name, Ct));
    }

    [Fact]
    public async Task ABranchCheckedOutInTheMainCheckoutIsRefusedNamingItsPathAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-1", "main", jobs, Ct));

        Assert.Contains("--branch main: already checked out in", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(repo), ex.Message, StringComparison.Ordinal);
        Assert.Equal("main", GitRepo.Run(repo, "branch", "--show-current"));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
    }

    [Fact]
    public async Task AWorktreeMadeByHandIsNeverRemovedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        string byHand = Path.Combine(scratch.CreateDirectory(), "hand");
        GitRepo.Run(repo, "worktree", "add", "-q", byHand, "-b", "feature/hand");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/hand", jobs, Ct));

        Assert.Contains("already checked out in", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hand", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(byHand));
    }

    [Fact]
    public async Task ARunningJobsWorktreeIsLeftIntactAndTheRefusalSaysItHasNotFinishedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo running = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));
        File.WriteAllText(Path.Combine(running.Path, "in-progress.txt"), "half done\n");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("the worktree of job job-1, which has not finished (no result.json)", ex.Message, StringComparison.Ordinal);
        Assert.Equal("half done\n", File.ReadAllText(Path.Combine(running.Path, "in-progress.txt")));
        Assert.Equal(2, GitRepo.WorktreePaths(repo).Length);
    }

    // R3: a job directory this CLAUSTRUM_HOME has never heard of is not "finished" — it may be running
    // under another home (an MCP server's environment against the shell's), and freeing its worktree would
    // let its later writes land in a plain directory inside the main checkout.
    [Fact]
    public async Task AJobThisJobsRootDoesNotKnowIsRefusedAndKeptWithTheCleanAdviceAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo foreign = await JobWorktree.AddAsync(repo, "job-1", Ct);

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("which is unknown under this CLAUSTRUM_HOME", ex.Message, StringComparison.Ordinal);
        Assert.Contains(jobs, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"claustrum jobs clean --cwd \"{repo}\"", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(foreign.Path));
    }

    [Fact]
    public async Task AFinishedJobsDirtyWorktreeIsKeptWithItsChangesAndTheRefusalNamesThemAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(first.Path, "seed.txt"), "edited by the role\n");
        Finish(jobs, "job-1");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("the worktree of finished job job-1, which was left in place", ex.Message, StringComparison.Ordinal);
        Assert.Contains("uncommitted changes:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("M seed.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("commit or discard its changes, then retry", ex.Message, StringComparison.Ordinal);
        Assert.Equal("edited by the role\n", File.ReadAllText(Path.Combine(first.Path, "seed.txt")));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-2")));
    }

    // R2: under status.showUntrackedFiles=no git's own remove check lets a worktree whose only change is a
    // new file go, deleting it silently (measured, git 2.56). Claustrum asks with --untracked-files=all first.
    [Fact]
    public async Task AFinishedWorktreeWhoseOnlyChangeIsANewFileIsKeptWhateverStatusShowUntrackedFilesSaysAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "config", "status.showUntrackedFiles", "no");
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(first.Path, "new.txt"), "only a new file\n");
        Finish(jobs, "job-1");
        Assert.Equal("", GitRepo.Run(first.Path, "status", "--porcelain"));

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("uncommitted changes: ?? new.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("only a new file\n", File.ReadAllText(Path.Combine(first.Path, "new.txt")));
        Assert.True(Directory.Exists(first.Path));
    }

    // Ignored files (bin/, obj/) are build output, not work: a plain `git worktree remove` takes them.
    [Fact]
    public async Task AFinishedWorktreeHoldingOnlyIgnoredFilesIsFreedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        File.AppendAllText(Path.Combine(repo, ".gitignore"), "*.log\n");
        GitRepo.Run(repo, "add", "-A");
        GitRepo.Run(repo, "commit", "-q", "-m", "ignore logs");
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(first.Path, "build.log"), "ignored\n");
        Finish(jobs, "job-1");

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    // T1: a free killed mid-remove can leave the branch held by an entry whose directory is gone. `git
    // worktree prune` drops exactly those, so the holder is no longer named as someone else's checkout.
    [Fact]
    public async Task AFinishedWorktreeWhoseDirectoryWasDeletedByHandIsPrunedAndTheBranchTakenAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Commit(first.Path, "work.txt", "kept on the branch\n");
        Finish(jobs, "job-1");
        TempTree.Delete(first.Path);
        Assert.Contains("job-1", RegisteredNames(repo));

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.Equal("kept on the branch\n", File.ReadAllText(Path.Combine(second.Path, "work.txt")));
        Assert.DoesNotContain("job-1", RegisteredNames(repo));
    }

    // prune leaves a locked entry alone (git's documented way to protect a removable drive's worktree), so
    // a locked stale holder is still somebody's checkout and is still refused.
    [Fact]
    public async Task ALockedStaleWorktreeEntryIsNotPrunedAndStillRefusesTheBranchAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Finish(jobs, "job-1");
        GitRepo.Run(repo, "worktree", "lock", first.Path, "--reason", "on a removable drive");
        TempTree.Delete(first.Path);

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("--branch claustrum/job-1: already checked out in", ex.Message, StringComparison.Ordinal);
        Assert.Contains("job-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    // git lists a worktree by its realpath. Added through a symlinked cwd, our own finished worktree would
    // look foreign if it were compared as typed.
    [Fact]
    public async Task AFinishedWorktreeAddedThroughASymlinkedCwdIsStillRecognisedAsOursAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("creating a directory symlink needs a privilege a Windows runner does not always grant.");
            return;
        }

        string repo = scratch.CreateSeededRepo();
        string link = Path.Combine(scratch.CreateDirectory(), "link");
        Directory.CreateSymbolicLink(link, repo);
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(link, "job-1", Ct);
        Commit(first.Path, "work.txt", "via the link\n");
        Finish(jobs, "job-1");

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(link, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(first.Path));
        Assert.Equal("via the link\n", File.ReadAllText(Path.Combine(second.Path, "work.txt")));
    }
}
