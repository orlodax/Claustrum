using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// Review 2026-10-14, round 2: no repo-wide `git worktree prune` anywhere. Only a dead job entry (its `.git` gone, unlocked,
// under this cwd's `.claustrum/worktrees`) is ever released, gitdir files are read against their admin directory
// (`worktree.useRelativePaths`), and a planted `.trash-*` can release nothing it did not come from.
[Collection(FakeGitCollectionDefinition.Name)]
public sealed class JobWorktreeFreeEntryTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-entry-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AdminOf(string worktree) => GitRepo.Run(worktree, "rev-parse", "--path-format=absolute", "--git-dir");

    private static string[] RegisteredNames(string repo) => [.. GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path))];

    private static void Finish(string jobsRoot, string jobId)
    {
        Directory.CreateDirectory(Path.Combine(jobsRoot, jobId));
        File.WriteAllText(Path.Combine(jobsRoot, jobId, "result.json"), "{}");
    }

    // An operator's worktree on a drive that is not mounted: unlocked, directory missing, a staged index and a detached commit.
    private (string Admin, string Name) Unmounted(string repo)
    {
        string path = Path.Combine(scratch.CreateDirectory(), "unmounted-drive");
        GitRepo.Run(repo, "worktree", "add", "-q", "--detach", path);
        File.WriteAllText(Path.Combine(path, "detached.txt"), "a commit on no branch\n");
        GitRepo.Run(path, "add", "-A");
        GitRepo.Run(path, "commit", "-q", "-m", "detached work");
        File.WriteAllText(Path.Combine(path, "staged.txt"), "staged\n");
        GitRepo.Run(path, "add", "staged.txt");
        string admin = AdminOf(path);
        TempTree.Delete(path);
        return (admin, "unmounted-drive");
    }

    private static void AssertKept(string repo, string admin, string name)
    {
        Assert.True(Directory.Exists(admin), "the operator's admin directory was deleted");
        Assert.True(File.Exists(Path.Combine(admin, "index")), "its staged index was deleted");
        Assert.Contains(name, RegisteredNames(repo));
    }

    [Fact]
    public async Task AFailedAddUndoKeepsAnUnrelatedUnmountedWorktreeAsync()
    {
        string repo = scratch.CreateSeededRepo();
        (string admin, string name) = Unmounted(repo);
        // The hook deletes the new worktree's `.git`, so the forced remove fails and the undo has an entry to release.
        GitRepo.WriteHook(repo, "post-checkout", "rm -f .git\nexit 1");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", Ct));

        Assert.Contains("job-1", ex.Message, StringComparison.Ordinal);
        AssertKept(repo, admin, name);
        Assert.DoesNotContain("job-1", RegisteredNames(repo));
    }

    [Fact]
    public async Task AJobWorktreeWhoseDirectoryWasDeletedByHandIsReleasedAndTheBranchTakenWhileAnUnmountedOneStaysAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(first.Path, "work.txt"), "work\n");
        GitRepo.Run(first.Path, "add", "-A");
        GitRepo.Run(first.Path, "commit", "-q", "-m", "work");
        Finish(jobs, "job-1");
        TempTree.Delete(first.Path);
        (string admin, string name) = Unmounted(repo);

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.True(File.Exists(Path.Combine(second.Path, "work.txt")));
        Assert.DoesNotContain("job-1", RegisteredNames(repo));
        AssertKept(repo, admin, name);
    }

    [Fact]
    public async Task AMissingDirectoryHolderOutsideTheJobsDirectoryIsRefusedAsSomeoneElsesAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        GitRepo.Run(repo, "branch", "feature/x");
        string elsewhere = Path.Combine(scratch.CreateDirectory(), "hand-made");
        GitRepo.Run(repo, "worktree", "add", "-q", elsewhere, "feature/x");
        string admin = AdminOf(elsewhere);
        TempTree.Delete(elsewhere);

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/x", jobs, Ct));

        Assert.Contains("already checked out in", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(admin));
        Assert.Contains("hand-made", RegisteredNames(repo));
    }

    [Fact]
    public async Task ALockedStaleJobHolderIsRefusedAndItsEntryStaysAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Finish(jobs, "job-1");
        GitRepo.Run(repo, "worktree", "lock", first.Path, "--reason", "removable drive");
        string admin = AdminOf(first.Path);
        TempTree.Delete(first.Path);

        await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.True(Directory.Exists(admin));
    }

    // ---- worktree.useRelativePaths (git 2.48) -------------------------------------------------------------------------

    private static void UseRelativePaths(string repo)
    {
        GitRepo.Run(repo, "config", "worktree.useRelativePaths", "true");
    }

    [Fact]
    public async Task AWorktreeWithRelativeGitdirFilesIsFreedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        UseRelativePaths(repo);
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        string gitdir = File.ReadAllText(Path.Combine(AdminOf(worktree.Path), "gitdir")).Trim();
        if (Path.IsPathRooted(gitdir))
            Assert.Skip("this git writes absolute gitdir paths even with worktree.useRelativePaths (older than 2.48).");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.False(Directory.Exists(worktree.Path));
        Assert.DoesNotContain("job-1", RegisteredNames(repo));
    }

    [Fact]
    public async Task ALeftoverTrashOfARelativePathWorktreeIsSweptAndItsEntryReleasedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        UseRelativePaths(repo);
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        string admin = AdminOf(worktree.Path);
        if (Path.IsPathRooted(File.ReadAllText(Path.Combine(admin, "gitdir")).Trim()))
            Assert.Skip("this git writes absolute gitdir paths even with worktree.useRelativePaths (older than 2.48).");

        string trash = Path.Combine(Path.GetDirectoryName(worktree.Path) ?? repo, ".trash-job-1-12345678");
        Directory.Move(worktree.Path, trash);

        (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(1, swept);
        Assert.Empty(failures);
        Assert.False(Directory.Exists(admin));
        Assert.False(Directory.Exists(trash));
    }

    // ---- a planted trash releases nothing it did not come from -------------------------------------------------------------

    private static string Plant(string repo, string name, string adminPath)
    {
        string trash = Directory.CreateDirectory(Path.Combine(repo, ".claustrum", "worktrees", name)).FullName;
        File.WriteAllText(Path.Combine(trash, ".git"), $"gitdir: {adminPath}\n");
        return trash;
    }

    [Fact]
    public async Task ATrashPointingAtAnUnmountedAbsolutePathWorktreeReleasesNothingAsync()
    {
        string repo = scratch.CreateSeededRepo();
        (string admin, string name) = Unmounted(repo);
        string trash = Plant(repo, ".trash-unmounted-drive-aaaaaaaa", admin);

        (int swept, _) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(1, swept);
        Assert.False(Directory.Exists(trash));
        AssertKept(repo, admin, name);
    }

    [Fact]
    public async Task ATrashPointingAtALiveWorktreeReleasesNothingAsync()
    {
        string repo = scratch.CreateSeededRepo();
        UseRelativePaths(repo);
        JobWorktreeInfo live = await JobWorktree.AddAsync(repo, "job-1", Ct);
        string admin = AdminOf(live.Path);
        Plant(repo, ".trash-job-1-bbbbbbbb", admin);

        await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.True(Directory.Exists(admin));
        Assert.True(File.Exists(Path.Combine(live.Path, "seed.txt")));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    // A worktree of this repository whose top lived in some other directory: the trash lies about where it came from.
    [Fact]
    public async Task ATrashPointingAtAnEntryWhoseTopLivedInAnotherDirectoryReleasesNothingAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string other = Path.Combine(repo, ".claustrum", "other", "job-1");
        Directory.CreateDirectory(Path.GetDirectoryName(other) ?? repo);
        GitRepo.Run(repo, "worktree", "add", "-q", "-b", "elsewhere", other);
        string admin = AdminOf(other);
        TempTree.Delete(other);
        Plant(repo, ".trash-job-1-cccccccc", admin);

        await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.True(Directory.Exists(admin));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    [Fact]
    public async Task AFifoDotGitInATrashIsReportedWithoutHangingAndTheTrashStaysAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string trash = Directory.CreateDirectory(Path.Combine(repo, ".claustrum", "worktrees", ".trash-job-1-dddddddd")).FullName;
        PipeFiles.MakeFifo(Path.Combine(trash, ".git"));

        (int swept, string[] failures) = await PipeFiles.WithinAsync(() => JobWorktree.SweepTrashAsync(repo, Ct));

        Assert.Equal(0, swept);
        Assert.Contains("not a regular file", Assert.Single(failures), StringComparison.Ordinal);
        Assert.True(Directory.Exists(trash));
    }

    // ---- failures of git itself --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWorktreeListThatCannotStartIsASweepFailureNotAThrowAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string trash = Directory.CreateDirectory(Path.Combine(repo, ".claustrum", "worktrees", ".trash-job-1-eeeeeeee")).FullName;

        (int swept, string[] failures) result;
        using (new NoGitOnPath())
            result = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(0, result.swept);
        Assert.Contains("no trash was swept", Assert.Single(result.failures), StringComparison.Ordinal);
        Assert.True(Directory.Exists(trash));
    }

    // An old git echoes `--path-format=absolute` back as if it were an answer; the free names the version it needs.
    [Fact]
    public async Task AGitThatDoesNotKnowPathFormatIsRefusedByNameAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);

        InvalidOperationException ex;
        using (new FakeGit("rev-parse", 0, alsoContains: "--path-format=absolute", echoArgs: true))
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.Contains("needs git 2.31 or later", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains("job-1", RegisteredNames(repo));
    }
}
