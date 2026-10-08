using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// F3/F9/R2: nothing that takes a worktree away may take uncommitted work with it. `jobs clean`'s
// RemoveAsync, a `--branch` run freeing a finished worktree and the abandoned-run cleanup all ask
// `git status --untracked-files=all` before a plain `git worktree remove` — git's own check honours
// `status.showUntrackedFiles=no` and then deletes new files silently (measured, git 2.56). Only the
// abandoned-run cleanup may force, and only when nothing of the run's can be in the worktree.
public sealed class JobWorktreeRemoveTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Commit(string worktree, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(worktree, fileName), content);
        GitRepo.Run(worktree, "add", "-A");
        GitRepo.Run(worktree, "commit", "-q", "-m", $"add {fileName}");
    }

    [Fact]
    public async Task RemoveRefusesAWorktreeWithAnUncommittedEditAndLeavesTheFileAndTheBranchAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "edited\n");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.StartsWith($"{worktree.Path} left in place: uncommitted changes:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("M seed.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task RemoveCountsTheOtherChangesInItsRefusalAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(worktree.Path, "b.txt"), "b\n");
        File.WriteAllText(Path.Combine(worktree.Path, "c.txt"), "c\n");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.Contains("uncommitted changes: ?? a.txt (and 2 more)", ex.Message, StringComparison.Ordinal);
    }

    // The premise first: with the option on, git's own porcelain shows nothing for a new file.
    [Fact]
    public async Task RemoveRefusesAWorktreeWhoseOnlyChangeIsANewFileUnderShowUntrackedFilesNoAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "config", "status.showUntrackedFiles", "no");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "new.txt"), "work\n");
        Assert.Equal("", GitRepo.Run(worktree.Path, "status", "--porcelain"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.Contains("uncommitted changes: ?? new.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("work\n", File.ReadAllText(Path.Combine(worktree.Path, "new.txt")));
    }

    [Fact]
    public async Task RemoveTakesAWorktreeHoldingOnlyIgnoredFilesAsync()
    {
        string repo = scratch.CreateSeededRepo();
        File.AppendAllText(Path.Combine(repo, ".gitignore"), "*.log\n");
        GitRepo.Run(repo, "add", "-A");
        GitRepo.Run(repo, "commit", "-q", "-m", "ignore logs");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "build.log"), "ignored\n");

        await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.False(Directory.Exists(worktree.Path));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
    }

    // A directory that git no longer knows as a worktree resolves, from inside, to the main checkout; the
    // main checkout's changes are not that directory's. The prefix check keeps them out of the verdict, and
    // git then refuses the path itself.
    [Fact]
    public async Task RemoveOfAPlainDirectoryNeverBlamesTheMainCheckoutsChangesAsync()
    {
        string repo = scratch.CreateSeededRepo();
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "the operator's edit\n");
        string plain = Path.Combine(repo, ".claustrum", "worktrees", "job-9");
        Directory.CreateDirectory(plain);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-9", Ct));

        Assert.Contains("left in place", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("uncommitted changes", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(plain));
        Assert.Equal("the operator's edit\n", File.ReadAllText(Path.Combine(repo, "seed.txt")));
    }

    [Fact]
    public async Task AnAbandonedUntouchedWorktreeIsForceRemovedAndItsOwnedBranchDeletedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.Null(failure);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    // F9: FinishAsync threw after the runner had committed (an unwritable result.json). The branch carries
    // the run's work now, so the cleanup that used to `branch -D` it would have destroyed it.
    [Fact]
    public async Task AnAbandonedWorktreeWhoseBranchMovedKeepsTheBranchAtItsNewTipAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Commit(worktree.Path, "work.txt", "committed by the runner\n");
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.Null(failure);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // The unmoved branch with a dirty worktree is a refused commit followed by an unwritable result.json:
    // the run's work is in that worktree only.
    [Fact]
    public async Task AnAbandonedDirtyWorktreeWhoseBranchDidNotMoveIsLeftWithItsWorkAndTheErrorIsReturnedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "the only copy\n");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        InvalidOperationException error = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains($"{worktree.Path} left in place, branch claustrum/job-1 kept", error.Message, StringComparison.Ordinal);
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(worktree.Path, "work.txt")));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task AnAbandonedWorktreeWhoseOnlyChangeIsANewFileIsKeptUnderShowUntrackedFilesNoAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "config", "status.showUntrackedFiles", "no");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "the only copy\n");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(worktree.Path, "work.txt")));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task AnAbandonedWorktreeThatIsBothMovedAndDirtyIsLeftAndItsBranchKeptAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Commit(worktree.Path, "work.txt", "committed\n");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "edited afterwards\n");
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("edited afterwards\n", File.ReadAllText(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // A worktree that is no longer on its branch cannot be proved untouched: a plain remove, no force, and
    // the branch is not deleted.
    [Fact]
    public async Task AnAbandonedWorktreeOffItsBranchIsPlainRemovedAndNoBranchIsDeletedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(worktree.Path, "switch", "-q", "-c", "other");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.Null(failure);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(repo));
        Assert.Contains("other", GitRepo.Branches(repo));
    }

    // #63: a `--branch` run was handed its branch; whatever happens to the run, the branch outlives it.
    [Fact]
    public async Task AnAbandonedBranchRunNeverHasItsBranchDeletedEvenWhenUntouchedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/given");
        string tip = GitRepo.RevParse(repo, "refs/heads/feature/given");
        JobWorktreeInfo worktree = await JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/given", scratch.CreateDirectory(), Ct);
        Assert.False(worktree.OwnsBranch);

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.Null(failure);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/feature/given"));
    }
}
