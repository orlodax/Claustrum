using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// On Unix git shares Claustrum's process group, so the terminal's Ctrl-C kills it before the token is cancelled and .NET reports
// 128 + the signal (measured 2026-10-13). Every git call of a free reads 129 (SIGHUP, #111), 130, 131 and 143 as an interrupted free: a cancelled
// receipt, the worktree intact, never "no such branch" or git saying no. Exit 1 and 128 are ordinary answers.
// A script named `git` first on PATH plays the interrupted git for one call and hands the rest to the real one, so the
// collection runs alone (PATH is process-wide).
[Collection(FakeGitCollectionDefinition.Name)]
public sealed class JobWorktreeInterruptedTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-interrupted-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, int> FreePathCalls()
    {
        TheoryData<string, int> data = [];
        foreach (string subcommand in new[] { "status", "symbolic-ref" })
        {
            foreach (int code in new[] { 129, 130, 131, 143 })
                data.Add(subcommand, code);
        }

        return data;
    }

    private async Task<(string Repo, JobWorktreeInfo Worktree)> FinishedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        return (repo, await JobWorktree.AddAsync(repo, "job-1", Ct));
    }

    [Theory]
    [MemberData(nameof(FreePathCalls))]
    public async Task AnInterruptedGitDuringRemoveIsAnInterruptedFreeAndTheWorktreeIsIntactAsync(string subcommand, int code)
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();

        using (new FakeGit(subcommand, code))
            await Assert.ThrowsAsync<WorktreeInterruptedException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.True(File.Exists(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Contains("job-1", GitRepo.WorktreePaths(repo).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repo, ".claustrum", "worktrees"), ".trash-*"));
    }

    [Theory]
    [InlineData("status", 1)]
    [InlineData("status", 128)]
    [InlineData("symbolic-ref", 128)]
    public async Task AnOrdinaryGitFailureDuringRemoveIsANormalRefusalAsync(string subcommand, int code)
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();

        InvalidOperationException ex;
        using (new FakeGit(subcommand, code))
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.Contains("left in place", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    // The --branch door's pre-gate check: BranchExistsAsync is `show-ref`, and an interrupted one is not "no such branch".
    [Theory]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(143)]
    public async Task AnInterruptedBranchProbeIsNotNoSuchBranchAsync(int code)
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/x");

        using (new FakeGit("show-ref", code))
            await Assert.ThrowsAsync<WorktreeInterruptedException>(() => JobWorktree.BranchExistsAsync(repo, "feature/x", Ct));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task AnOrdinaryFailureOfTheBranchProbeReadsAsNoSuchBranchAsync(int code)
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/x");

        using (new FakeGit("show-ref", code))
            Assert.False(await JobWorktree.BranchExistsAsync(repo, "feature/x", Ct));
    }

    // The same door, the free itself: the tip read after the free, and the free's own checks, both interrupted.
    [Theory]
    [InlineData(129)]
    [InlineData(130)]
    [InlineData(131)]
    [InlineData(143)]
    public async Task AnInterruptedTipReadInCheckOutBranchIsInterruptedNotARefusalAsync(int code)
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Run(repo, "branch", "feature/x");

        using (new FakeGit("rev-parse", code, alsoContains: "--quiet"))
            await Assert.ThrowsAsync<WorktreeInterruptedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-9", "feature/x", scratch.CreateDirectory(), Ct));

        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-9")));
    }

    [Theory]
    [InlineData("status", 130)]
    [InlineData("symbolic-ref", 143)]
    [InlineData("status", 129)]
    public async Task AnInterruptedFreeInsideCheckOutBranchLeavesTheFinishedWorktreeIntactAsync(string subcommand, int code)
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        string jobs = scratch.CreateDirectory();
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));
        File.WriteAllText(Path.Combine(jobs, "job-1", "result.json"), "{}");

        using (new FakeGit(subcommand, code))
            await Assert.ThrowsAsync<WorktreeInterruptedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.True(File.Exists(Path.Combine(worktree.Path, "seed.txt")));
        Assert.Contains("job-1", GitRepo.WorktreePaths(repo).Select(Path.GetFileName));
    }

    // Best effort by contract: a caller passing no token still gets the cancel back as a value.
    [Fact]
    public async Task TryRemoveAbandonedNeverThrowsACancelToACallerWithNoTokenAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await FinishedAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "uncommitted\n");

        Exception? failure;
        using (new FakeGit("symbolic-ref", 130))
            failure = await JobWorktree.TryRemoveAbandonedAsync(repo, worktree, CancellationToken.None);

        Assert.IsType<WorktreeInterruptedException>(failure);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "work.txt")));
    }
}
