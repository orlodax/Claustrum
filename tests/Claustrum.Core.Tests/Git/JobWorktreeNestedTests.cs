using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #74 trap 2: a `coordinate` architect's worktree holds its children's under its own `.claustrum/worktrees/`, ignored
// by its status — and git's plain `worktree remove` of it exits 0 while deleting those children, an uncommitted file
// included (measured 2026-10-09, git 2.56). Every plain remove in JobWorktree (`jobs clean`, `--branch` freeing a
// finished worktree, the abandoned-run cleanup) therefore refuses a worktree that holds a directory with a `.git` of
// its own. H1 (round 3): a directory WITHOUT one — the empty `OLD/` a `160000` entry in HEAD checks out in every
// worktree — holds nothing, and used to block every cleanup of every worktree cut from that HEAD for good.
public sealed class JobWorktreeNestedTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Nested(JobWorktreeInfo parent) => Path.Combine(parent.Path, ".claustrum", "worktrees");

    private static string CleanSweepAdvice(JobWorktreeInfo parent) => $"run `claustrum jobs clean --cwd \"{parent.Path}\"` first";

    private async Task<(string Repo, JobWorktreeInfo Architect, JobWorktreeInfo Child)> ArchitectWithAChildAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo architect = await JobWorktree.AddAsync(repo, "architect-1", Ct);
        // Cut from inside the architect's worktree, as an isolated builder's `claustrum run --cwd <it>` does.
        JobWorktreeInfo child = await JobWorktree.AddAsync(architect.Path, "child-1", Ct);
        return (repo, architect, child);
    }

    [Fact]
    public async Task AChildCutFromInsideTheArchitectsWorktreeNestsUnderItAndStartsFromTheWorkBranchTipAsync()
    {
        (string repo, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();

        Assert.Equal(Path.Combine(Nested(architect), "child-1"), child.Path);
        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/architect-1"), child.BaseCommit);
        Assert.Equal(architect.Path, child.MainCheckout);
        Assert.Contains("child-1", GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path)));
        Assert.Equal("", GitRepo.Status(architect.Path));
    }

    [Fact]
    public async Task RemoveRefusesAWorktreeHoldingARealNestedOneAndLeavesTheChildsUncommittedFileAsync()
    {
        (string repo, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();
        File.WriteAllText(Path.Combine(child.Path, "wip.txt"), "the only copy\n");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "architect-1", Ct));

        Assert.Equal($"{architect.Path} left in place: it holds job worktrees of its own under {Nested(architect)} — {CleanSweepAdvice(architect)}", ex.Message);
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(child.Path, "wip.txt")));
        Assert.True(Directory.Exists(architect.Path));
        Assert.Contains("claustrum/architect-1", GitRepo.Branches(repo));
    }

    // The documented order: the children's own sweep first (`--cwd <architect's worktree>`), the architect's after.
    [Fact]
    public async Task SweepingTheChildFirstThenTheArchitectRemovesBothAndKeepsBothBranchesAsync()
    {
        (string repo, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();

        await JobWorktree.RemoveAsync(architect.Path, "child-1", Ct);
        Assert.False(Directory.Exists(child.Path));
        await JobWorktree.RemoveAsync(repo, "architect-1", Ct);

        Assert.False(Directory.Exists(architect.Path));
        Assert.Single(GitRepo.WorktreePaths(repo));
        Assert.Contains("claustrum/child-1", GitRepo.Branches(repo));
        Assert.Contains("claustrum/architect-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task AChildWithUncommittedChangesIsNotSweptAndKeepsItsFileAsync()
    {
        (_, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();
        File.WriteAllText(Path.Combine(child.Path, "wip.txt"), "unfinished\n");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(architect.Path, "child-1", Ct));

        Assert.Contains("uncommitted changes: ?? wip.txt", ex.Message, StringComparison.Ordinal);
        Assert.Equal("unfinished\n", File.ReadAllText(Path.Combine(child.Path, "wip.txt")));
    }

    // The abandoned-run cleanup may force only a worktree provably untouched; one that holds a child is not.
    [Fact]
    public async Task TheAbandonedRunCleanupNeverForcesAnArchitectWorktreeThatHoldsAChildAsync()
    {
        (string repo, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();
        File.WriteAllText(Path.Combine(child.Path, "wip.txt"), "the only copy\n");

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, architect, Ct);

        InvalidOperationException error = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains($"{architect.Path} left in place, branch claustrum/architect-1 kept", error.Message, StringComparison.Ordinal);
        Assert.Contains("it holds job worktrees of its own", error.Message, StringComparison.Ordinal);
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(child.Path, "wip.txt")));
        Assert.Contains("claustrum/architect-1", GitRepo.Branches(repo));
    }

    // `--branch` freeing a finished architect's worktree meets the same refusal, worded as any left-in-place one.
    [Fact]
    public async Task FreeingAnArchitectsBranchWhileItHoldsAChildIsRefusedNamingTheSweepAsync()
    {
        (string repo, JobWorktreeInfo architect, _) = await ArchitectWithAChildAsync();
        string jobs = scratch.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(jobs, "architect-1"));
        File.WriteAllText(Path.Combine(jobs, "architect-1", "result.json"), "{}");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/architect-1", jobs, Ct));

        Assert.Contains("the worktree of finished job architect-1, which was left in place (it holds job worktrees of its own", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CleanSweepAdvice(architect), ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(architect.Path));
    }

    [Fact]
    public async Task AnEmptyWorktreesDirectoryIsNotANestedWorktreeAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo architect = await JobWorktree.AddAsync(repo, "architect-1", Ct);
        Directory.CreateDirectory(Nested(architect));

        await JobWorktree.RemoveAsync(repo, "architect-1", Ct);

        Assert.False(Directory.Exists(architect.Path));
    }

    [Fact]
    public async Task APlainDirectoryUnderWorktreesWithoutAGitFileIsNotANestedWorktreeAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo architect = await JobWorktree.AddAsync(repo, "architect-1", Ct);
        Directory.CreateDirectory(Path.Combine(Nested(architect), "just-a-folder"));

        await JobWorktree.RemoveAsync(repo, "architect-1", Ct);

        Assert.False(Directory.Exists(architect.Path));
    }

    // H1: HEAD carries `160000 … .claustrum/worktrees/OLD` (someone ran `add -A` while a job worktree existed and
    // nothing ignored it), so every worktree cut from it checks out an empty `OLD/`.
    private string RepoWhoseHeadCarriesAGitlinkUnderWorktrees()
    {
        string repo = scratch.CreateRepo();
        GitRepo.Commit(repo, "seed.txt", "seed\n");
        GitRepo.Run(repo, "update-index", "--add", "--cacheinfo", $"160000,{GitRepo.Head(repo)},.claustrum/worktrees/OLD");
        GitRepo.Run(repo, "commit", "-q", "-m", "a gitlink that should never have been committed");
        Assert.Equal("160000", GitRepo.Run(repo, "ls-tree", "HEAD", "--", ".claustrum/worktrees/OLD").Split(' ')[0]);
        return repo;
    }

    [Fact]
    public async Task TheEmptyDirectoryAGitlinkInHeadChecksOutDoesNotBlockRemoveAsync()
    {
        string repo = RepoWhoseHeadCarriesAGitlinkUnderWorktrees();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        string old = Path.Combine(Nested(worktree), "OLD");
        Assert.True(Directory.Exists(old), "the premise: the gitlink's empty directory is checked out in the new worktree");
        Assert.False(Path.Exists(Path.Combine(old, ".git")));
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "done\n");
        GitRepo.Run(worktree.Path, "add", "work.txt");
        GitRepo.Run(worktree.Path, "commit", "-q", "-m", "work");

        await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task TheEmptyGitlinkDirectoryDoesNotBlockTheAbandonedRunCleanupFromForcingAsync()
    {
        string repo = RepoWhoseHeadCarriesAGitlinkUnderWorktrees();
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);

        Exception? failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, Ct);

        Assert.Null(failure);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
    }

    [Fact]
    public async Task TheEmptyGitlinkDirectoryDoesNotBlockFreeingTheBranchAsync()
    {
        string repo = RepoWhoseHeadCarriesAGitlinkUnderWorktrees();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));
        File.WriteAllText(Path.Combine(jobs, "job-1", "result.json"), "{}");

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    // The deleted-admin row of the table: the nested directory still has its `.git` file, but git has no registration
    // and refuses to work in it. A plain remove of the parent would still delete whatever is there.
    [Fact]
    public async Task ANestedDirectoryWithAGitFileButNoRegistrationIsStillRefusedAsync()
    {
        (string repo, JobWorktreeInfo architect, JobWorktreeInfo child) = await ArchitectWithAChildAsync();
        TempTree.Delete(Path.Combine(repo, ".git", "worktrees", "child-1"));
        Assert.True(File.Exists(Path.Combine(child.Path, ".git")));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "architect-1", Ct));

        Assert.Contains("it holds job worktrees of its own", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(architect.Path));
        Assert.True(Directory.Exists(child.Path));
    }
}
