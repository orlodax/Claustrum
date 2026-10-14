using System.Runtime.Versioning;
using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #106 (final design): a finished job's worktree is freed by renaming it aside and then releasing only its own git entry; it
// is refused, intact, whenever it might hold work or is not this repository's worktree. Every case runs against a real
// repository and real `git worktree`. The refusals come back as InvalidOperationException ("<path> left in place: ...") from
// RemoveAsync; the file system after each is the assertion that matters.
public sealed class JobWorktreeFreeTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-free-");
    private readonly List<string> lockedDirectories = [];

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in lockedDirectories.Where(Directory.Exists))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        scratch.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Repo, JobWorktreeInfo Worktree)> FinishedAsync(string jobId = "job-1")
    {
        string repo = scratch.CreateSeededRepo();
        return (repo, await JobWorktree.AddAsync(repo, jobId, Ct));
    }

    private static void Commit(string worktree, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(worktree, fileName), content);
        GitRepo.Run(worktree, "add", "-A");
        GitRepo.Run(worktree, "commit", "-q", "-m", $"add {fileName}");
    }

    private static string AdminOf(string worktree) => GitRepo.Run(worktree, "rev-parse", "--path-format=absolute", "--git-dir");

    private static string[] Trash(string repo) =>
        Directory.Exists(Path.Combine(repo, ".claustrum", "worktrees"))
            ? [.. Directory.EnumerateFileSystemEntries(Path.Combine(repo, ".claustrum", "worktrees"), ".trash-*")]
            : [];

    private static async Task<string> RefusalAsync(string repo, string jobId = "job-1")
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, jobId, Ct));
        return ex.Message;
    }

    // Registered names, as git lists them (a realpath): compared by the unique directory name.
    private static string[] RegisteredNames(string repo) => [.. GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path))];

    [UnsupportedOSPlatform("windows")]
    private void LockDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        lockedDirectories.Add(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string probe = Path.Combine(directory, "probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            Assert.Skip("this account is not stopped by a read-only directory (root, or a capability that bypasses modes).");
        }
        catch (UnauthorizedAccessException)
        {
            // The mode is enforced: the case can run.
        }
    }

    [Fact]
    public async Task ACleanFinishedWorktreeIsGoneWithNoTrashOnlyTheMainCheckoutIsListedAndTheBranchIsKeptAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        Commit(worktree.Path, "work.txt", "kept\n");

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.Null(warning);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(Trash(repo));
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(AdminOf(repo), "worktrees")));
    }

    [Fact]
    public async Task ALockedWorktreeIsRefusedAndIntactAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(repo, "worktree", "lock", worktree.Path, "--reason", "somebody's");

        string message = await RefusalAsync(repo);

        Assert.Contains("locked", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    [Fact]
    public async Task APlainDirectoryWithNoDotGitIsLeftInPlaceNotBlamedOnUncommittedChangesAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string plain = Directory.CreateDirectory(JobWorktree.PathFor(repo, "job-1")).FullName;
        File.WriteAllText(Path.Combine(plain, "notes.txt"), "mine\n");

        string message = await RefusalAsync(repo);

        Assert.Contains("left in place", message, StringComparison.Ordinal);
        Assert.DoesNotContain("uncommitted changes", message, StringComparison.Ordinal);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(plain, "notes.txt")));
    }

    [Fact]
    public async Task AWorktreeWhoseDotGitWasDeletedIsLeftInPlaceAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        File.Delete(Path.Combine(worktree.Path, ".git"));

        string message = await RefusalAsync(repo);

        Assert.Contains("left in place", message, StringComparison.Ordinal);
        Assert.DoesNotContain("uncommitted changes", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task ASymbolicLinkInPlaceOfTheWorktreeDirectoryIsRefusedAndTheTargetUntouchedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        string moved = Path.Combine(scratch.CreateDirectory(), "moved");
        Directory.Move(worktree.Path, moved);
        try
        {
            Directory.CreateSymbolicLink(worktree.Path, moved);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }

        string message = await RefusalAsync(repo);

        Assert.Contains("symbolic link", message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(moved, "seed.txt")));
        Assert.NotNull(new DirectoryInfo(worktree.Path).LinkTarget);
    }

    [Fact]
    public async Task AMissingDirectoryIsRefusedAsNotADirectoryAsync()
    {
        string repo = scratch.CreateSeededRepo();

        string message = await RefusalAsync(repo);

        Assert.Contains("not a directory", message, StringComparison.Ordinal);
    }

    // A role that ran `mv .git x; git init`: the directory still hangs off the admin entry, but its repository is the role's.
    [Fact]
    public async Task AWorktreeWhoseDotGitWasReplacedByARepositoryOfItsOwnIsNotThisRepositorysAndKeepsItsCommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        Directory.Move(Path.Combine(worktree.Path, ".git"), Path.Combine(scratch.CreateDirectory(), "old-dot-git"));
        GitRepo.Init(worktree.Path);
        Commit(worktree.Path, "private.txt", "only here\n");
        string commit = GitRepo.Head(worktree.Path);

        string message = await RefusalAsync(repo);

        Assert.Contains("not this repository's worktree", message, StringComparison.Ordinal);
        Assert.Equal(commit, GitRepo.Head(worktree.Path));
        Assert.Equal("only here\n", File.ReadAllText(Path.Combine(worktree.Path, "private.txt")));
    }

    [Fact]
    public async Task ADotGitFilePointingAtAnotherWorktreesAdminDirectoryIsRefusedAsync()
    {
        (string repo, JobWorktreeInfo first) = await FinishedAsync();
        JobWorktreeInfo second = await JobWorktree.AddAsync(repo, "job-2", Ct);
        File.WriteAllText(Path.Combine(first.Path, ".git"), $"gitdir: {AdminOf(second.Path)}\n");

        string message = await RefusalAsync(repo);

        Assert.Contains("not this repository's worktree", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    [Fact]
    public async Task ADetachedHeadWithACommitIsRefusedAndIntactAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");
        Commit(worktree.Path, "lost.txt", "on no branch\n");
        string commit = GitRepo.Head(worktree.Path);

        string message = await RefusalAsync(repo);

        Assert.Contains("detached HEAD", message, StringComparison.Ordinal);
        Assert.Equal(commit, GitRepo.Head(worktree.Path));
    }

    // Review 2026-10-14, round 2: commits on any existing branch live in the shared refs, so a HEAD on another branch frees.
    [Fact]
    public async Task AHeadOnAnotherExistingBranchIsFreedAndThatBranchKeepsItsCommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "-b", "other");
        Commit(worktree.Path, "elsewhere.txt", "on other\n");

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.Null(warning);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal("elsewhere.txt", GitRepo.Run(repo, "show", "--name-only", "--format=", "other"));
    }

    [Fact]
    public async Task AHeadNamingABranchThatDoesNotExistIsRefusedAndIntactAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--orphan", "unborn");

        string message = await RefusalAsync(repo);

        Assert.Contains("refs/heads/unborn", message, StringComparison.Ordinal);
        Assert.Contains("does not exist", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    [Theory]
    [InlineData("MERGE_HEAD", false)]
    [InlineData("CHERRY_PICK_HEAD", false)]
    [InlineData("REVERT_HEAD", false)]
    [InlineData("BISECT_LOG", false)]
    [InlineData("rebase-merge", true)]
    [InlineData("rebase-apply", true)]
    [InlineData("sequencer", true)]
    public async Task AnOperationInProgressInTheAdminDirectoryIsRefusedAndIntactAsync(string marker, bool directory)
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        string path = Path.Combine(AdminOf(worktree.Path), marker);
        if (directory)
            Directory.CreateDirectory(path);
        else
            File.WriteAllText(path, GitRepo.Head(repo) + "\n");

        string message = await RefusalAsync(repo);

        Assert.Contains("in progress", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    [Theory]
    [InlineData("--skip-worktree")]
    [InlineData("--assume-unchanged")]
    public async Task AHiddenEditUnderSkipWorktreeOrAssumeUnchangedIsRefusedAndKeptAsync(string flag)
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "update-index", flag, "seed.txt");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "edited and hidden\n");
        Assert.Equal("", GitRepo.Status(worktree.Path));

        string message = await RefusalAsync(repo);

        Assert.Contains("skip-worktree or assume-unchanged", message, StringComparison.Ordinal);
        Assert.Equal("edited and hidden\n", File.ReadAllText(Path.Combine(worktree.Path, "seed.txt")));
    }

    [Fact]
    public async Task ASparseCheckoutWhoseExcludedFilesAreAbsentFromDiskIsStillFreedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        Directory.CreateDirectory(Path.Combine(repo, "a"));
        Directory.CreateDirectory(Path.Combine(repo, "b"));
        File.WriteAllText(Path.Combine(repo, "a", "x.txt"), "x\n");
        File.WriteAllText(Path.Combine(repo, "b", "y.txt"), "y\n");
        GitRepo.Run(repo, "add", "-A");
        GitRepo.Run(repo, "commit", "-q", "-m", "two dirs");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(worktree.Path, "sparse-checkout", "set", "--cone", "a");
        Assert.False(File.Exists(Path.Combine(worktree.Path, "b", "y.txt")));
        Assert.Contains("S ", GitRepo.Run(worktree.Path, "ls-files", "-v"), StringComparison.Ordinal);

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.Null(warning);
        Assert.False(Directory.Exists(worktree.Path));
    }

    // The earlier design wrote a `claustrum-removing` marker that made a dirty tree look half removed; it is gone, and a
    // stale file of that name must never loosen a check.
    [Fact]
    public async Task AStaleRemovingMarkerInTheAdminDirectoryNeverLoosensTheDirtyCheckAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        File.WriteAllText(Path.Combine(AdminOf(worktree.Path), "claustrum-removing"), "stale\n");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "dirty\n");

        string message = await RefusalAsync(repo);

        Assert.Contains("uncommitted changes", message, StringComparison.Ordinal);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    // An operator's worktree on a drive that is not mounted: its entry, with the staged index and the detached commit inside
    // the admin directory, is the only record of that work. A free drops its own entry and nothing else.
    private async Task<(string Admin, string Name)> UnmountedWorktreeAsync(string repo)
    {
        string path = Path.Combine(scratch.CreateDirectory(), "unmounted-drive");
        GitRepo.Run(repo, "worktree", "add", "-q", "--detach", path);
        Commit(path, "detached.txt", "a commit on no branch\n");
        File.WriteAllText(Path.Combine(path, "staged.txt"), "staged\n");
        GitRepo.Run(path, "add", "staged.txt");
        string admin = AdminOf(path);
        TempTree.Delete(path);
        Assert.True(Directory.Exists(admin));
        return (admin, "unmounted-drive");
    }

    private static void AssertUnmountedEntryKept(string repo, string admin, string name)
    {
        Assert.True(Directory.Exists(admin), "the unrelated worktree's admin directory was deleted");
        Assert.True(File.Exists(Path.Combine(admin, "index")), "its staged index was deleted");
        Assert.Contains(name, RegisteredNames(repo));
    }

    [Fact]
    public async Task AnUnrelatedWorktreeWhoseDirectoryIsMissingKeepsItsEntryWhenAnotherIsFreedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        (string admin, string name) = await UnmountedWorktreeAsync(repo);

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.Null(warning);
        Assert.False(Directory.Exists(worktree.Path));
        AssertUnmountedEntryKept(repo, admin, name);
    }

    [Fact]
    public async Task AnUnrelatedWorktreeWhoseDirectoryIsMissingKeepsItsEntryWhenBranchFreesAFinishedOneAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        Commit(worktree.Path, "work.txt", "work\n");
        string jobs = scratch.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));
        File.WriteAllText(Path.Combine(jobs, "job-1", "result.json"), "{}");
        (string admin, string name) = await UnmountedWorktreeAsync(repo);

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.True(File.Exists(Path.Combine(second.Path, "work.txt")));
        AssertUnmountedEntryKept(repo, admin, name);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task ARenameThatFailsBecauseTheParentIsNotWritableIsARefusalAndLeavesTheWorktreeIntactAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        LockDirectory(Path.GetDirectoryName(worktree.Path) ?? repo);

        string message = await RefusalAsync(repo);

        Assert.Contains("could not be moved aside", message, StringComparison.Ordinal);
        Assert.Contains("intact", message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    // The rename succeeded but the admin directory cannot be deleted: freed, but the leftovers are named in a warning, not an error.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AnAdminDirectoryThatCannotBeDeletedAfterTheRenameIsAWarningNotARefusalAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        string stuck = Directory.CreateDirectory(Path.Combine(AdminOf(worktree.Path), "stuck")).FullName;
        File.WriteAllText(Path.Combine(stuck, "file"), "x");
        LockDirectory(stuck);

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.NotNull(warning);
        Assert.Contains("freed, but", warning, StringComparison.Ordinal);
        Assert.Contains("trash at", warning, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Single(Trash(repo));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task CheckOutBranchProceedsAfterAFreeWhoseAdminDirectoryCouldNotBeDeletedAndCarriesTheWarningAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        Commit(worktree.Path, "work.txt", "work\n");
        string jobs = scratch.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));
        File.WriteAllText(Path.Combine(jobs, "job-1", "result.json"), "{}");
        string stuck = Directory.CreateDirectory(Path.Combine(AdminOf(worktree.Path), "stuck")).FullName;
        File.WriteAllText(Path.Combine(stuck, "file"), "x");
        LockDirectory(stuck);

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.NotNull(second.FreeWarning);
        Assert.Contains("freed, but", second.FreeWarning, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(second.Path, "work.txt")));
    }

    [Fact]
    public async Task ABranchJobsWorktreeIsFreedWhateverBranchItIsOnAndHeadBranchNamesItAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        GitRepo.Run(repo, "branch", "feature/x");
        JobWorktreeInfo info = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "feature/x", jobs, Ct);

        string? head = await JobWorktree.HeadBranchAsync(repo, "job-2", Ct);
        string? warning = await JobWorktree.RemoveAsync(repo, "job-2", Ct);

        Assert.Equal("feature/x", head);
        Assert.Null(warning);
        Assert.False(Directory.Exists(info.Path));
        Assert.Contains("feature/x", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task HeadBranchIsNullForADetachedHeadAndForAMissingDirectoryAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");

        Assert.Null(await JobWorktree.HeadBranchAsync(repo, "job-1", Ct));
        Assert.Null(await JobWorktree.HeadBranchAsync(repo, "no-such-job", Ct));
    }

    [Fact]
    public void IsTrashIsTrueForTrashNamesAndFalseForAJobId()
    {
        Assert.True(JobWorktree.IsTrash(".trash-x"));
        Assert.True(JobWorktree.IsTrash(".trash-job-1-ab12cd34"));
        Assert.False(JobWorktree.IsTrash("job-1"));
        Assert.False(JobWorktree.IsTrash("trash-x"));
    }

    // TryRemoveAbandonedAsync: an abandoned worktree on a detached HEAD is not force-removed; the free's refusal rides on
    // the returned exception, never thrown.
    [Fact]
    public async Task AnAbandonedWorktreeOnADetachedHeadComesBackAsAReturnedExceptionNamingTheRefusalAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("left in place", failure.Message, StringComparison.Ordinal);
        Assert.Contains("detached HEAD", failure.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
    }
}
