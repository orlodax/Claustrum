using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// T1: a `git worktree add` that fails, is killed or is cancelled used to leave a registered, sometimes
// `locked initializing`, worktree behind that no later `--branch` could take, `jobs clean` skipped (the job
// never wrote a result.json) and `branch -D` refused. TryAddAsync now undoes every failure of the add; these
// tests make the add fail the ways git can, with the repo's own post-checkout hook and a slow smudge filter,
// and read `git worktree list` and the branch list back.
public sealed class JobWorktreeUndoTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A hook that exits non-zero after the checkout: measured on git 2.56, the worktree is left registered
    // and checked out (exit 3), which is the case the undo exists for.
    [Fact]
    public async Task AFailedAddOnANewBranchLeavesNoWorktreeAndNoBranchAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.WriteHook(repo, "post-checkout", "exit 3");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", Ct));

        Assert.Contains("git worktree add", ex.Message, StringComparison.Ordinal);
        Assert.Contains("failed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("left behind", ex.Message, StringComparison.Ordinal);
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task AFailedAddOnAnExistingBranchLeavesNoWorktreeAndTheBranchAtTheSameShaAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/keep");
        string tip = GitRepo.RevParse(repo, "refs/heads/feature/keep");
        GitRepo.WriteHook(repo, "post-checkout", "exit 3");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/keep", scratch.CreateDirectory(), Ct));

        Assert.StartsWith("--branch feature/keep:", ex.Message, StringComparison.Ordinal);
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.Contains("feature/keep", GitRepo.Branches(repo));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/feature/keep"));
    }

    // `UndoAddAsync` forces the remove (`-f -f`) because nothing of a role's can be in a worktree the role
    // never ran in, and a post-checkout hook's leftovers, which a plain remove refuses, go too.
    [Fact]
    public async Task AHookThatLeavesFilesBehindDoesNotStopTheUndoAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.WriteHook(repo, "post-checkout", "echo leftover > hook-output.txt\nexit 1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", Ct));

        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    // The repo-wide `git worktree prune` runs only after a failed remove, because it also unregisters an
    // operator's unlocked worktree whose directory happens to be missing (an unmounted drive). Here the
    // remove succeeds, so the unrelated stale entry must survive.
    [Fact]
    public async Task APruneDoesNotRunWhenTheForcedRemoveSucceededAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string stale = Path.Combine(scratch.CreateDirectory(), "stale");
        GitRepo.Run(repo, "worktree", "add", "-q", stale, "-b", "stale-branch");
        TempTree.Delete(stale);
        GitRepo.WriteHook(repo, "post-checkout", "exit 1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", Ct));

        Assert.Contains("stale", GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path)));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    // The failing remove: a hook that deletes the new worktree's `.git` file makes `worktree remove -f -f`
    // exit 128 ("validation failed … '.git' does not exist", measured), which leaves the entry registered
    // `prunable`. Only then does the undo prune — and the prune, being repository-wide, takes the unrelated
    // stale entry with it. The directory git could not remove is reported as left behind, the branch is
    // deleted once nothing holds it.
    [Fact]
    public async Task APruneRunsWhenTheForcedRemoveFailedAndTheDirectoryItCouldNotRemoveIsReportedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string stale = Path.Combine(scratch.CreateDirectory(), "stale");
        GitRepo.Run(repo, "worktree", "add", "-q", stale, "-b", "stale-branch");
        TempTree.Delete(stale);
        GitRepo.WriteHook(repo, "post-checkout", "rm -f .git\nexit 1");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", Ct));

        Assert.Contains($"left behind: {JobWorktree.PathFor(repo, "job-1")}", ex.Message, StringComparison.Ordinal);
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
        Assert.True(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
    }

    [Fact]
    public async Task AFailedAddOnAnExistingBranchThatLeftADirectoryBehindSaysSoAndKeepsTheBranchAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/keep");
        GitRepo.WriteHook(repo, "post-checkout", "rm -f .git\nexit 1");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/keep", scratch.CreateDirectory(), Ct));

        Assert.Contains("; left behind:", ex.Message, StringComparison.Ordinal);
        Assert.Contains(JobWorktree.PathFor(repo, "job-1"), ex.Message, StringComparison.Ordinal);
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.Contains("feature/keep", GitRepo.Branches(repo));
    }

    // A checkout that never finishes in time: a smudge filter that sleeps. The cancel kills git's process
    // tree mid-checkout, which leaves the worktree registered `locked initializing` (reproduced on git 2.56);
    // `remove`, `remove --force` and `branch -D` all refuse that, so only `-f -f` clears it.
    [Fact]
    public async Task ACancelMidCheckoutOfANewBranchLeavesNothingBehindAndStaysACancelAsync()
    {
        RequireSleepingFilter();
        string repo = SlowCheckoutRepo();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(1500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.AddAsync(repo, "job-1", cancel.Token));

        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task ACancelMidCheckoutOfAnExistingBranchLeavesNothingBehindAndTheBranchIntactAsync()
    {
        RequireSleepingFilter();
        string repo = SlowCheckoutRepo();
        GitRepo.Run(repo, "branch", "feature/keep");
        string tip = GitRepo.RevParse(repo, "refs/heads/feature/keep");
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(1500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-1", "feature/keep", scratch.CreateDirectory(), cancel.Token));

        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/feature/keep"));
    }

    private static void RequireSleepingFilter()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the slow smudge filter is `sleep 10; cat` run by git's sh; its availability on a Windows runner is not verified.");
    }

    // `*.txt filter=slow` with a smudge that sleeps 10 s: the checkout of seed.txt into a new worktree
    // takes that long, and the main checkout (already checked out) does not.
    private string SlowCheckoutRepo()
    {
        string repo = scratch.CreateRepo();
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".claustrum/\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.txt filter=slow\n");
        GitRepo.Run(repo, "config", "filter.slow.clean", "cat");
        GitRepo.Run(repo, "config", "filter.slow.smudge", "sleep 10; cat");
        GitRepo.Commit(repo, "seed.txt", "seed\n");
        return repo;
    }
}
