using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #74 round 4–5 (J4): `run --branch` frees a finished job's clean worktree by removing it — and git refuses to remove ANY
// worktree with a populated submodule, before its own clean check and in the user's language (`fatal: working trees
// containing submodules cannot be moved or removed`, exit 128). `FreeBranchAsync` therefore asks first, the way git's own
// `validate_no_submodules` does — the worktree's own `modules` directory, or a gitlink in its index whose directory
// holds a `.git` — and, for a clean worktree, says so with the one remedy that works (`git worktree remove --force`, with
// the warning that it deletes any commit made inside the submodule). A dirty worktree gets the generic text instead:
// `--force` would destroy its work. The ordering checks that come before (unknown job, unfinished job) are unchanged.
public sealed class JobWorktreeSubmoduleFreeTests : IDisposable
{
    private const int Cap = 1_000_000;

    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Finish(string jobsRoot, string jobId)
    {
        Directory.CreateDirectory(Path.Combine(jobsRoot, jobId));
        File.WriteAllText(Path.Combine(jobsRoot, jobId, "result.json"), "{}");
    }

    private static void AddSubmodule(string where, string source, string path) =>
        GitRepo.Run(where, "-c", "protocol.file.allow=always", "submodule", "add", "-q", source, path);

    // A repository whose HEAD carries a submodule `sub`; the main checkout has it initialised (its `.git/modules/sub`).
    private (string Repo, string Jobs) RepoWithASubmodule()
    {
        string repo = scratch.CreateSeededRepo();
        AddSubmodule(repo, scratch.CreateSeededRepo(), "sub");
        GitRepo.Run(repo, "commit", "-q", "-m", "add sub");
        return (repo, scratch.CreateDirectory());
    }

    // The finished job: a worktree that initialised `sub` and had its leftovers committed by the runner.
    private static async Task<JobWorktreeInfo> FinishedJobThatInitialisedTheSubmoduleAsync(string repo, string jobs, bool dirty)
    {
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(first.Path, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q", "sub");
        File.WriteAllText(Path.Combine(first.Path, "real.txt"), "work\n");
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(first, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);
        Assert.NotNull(receipt.Commit);
        if (dirty)
            File.WriteAllText(Path.Combine(first.Path, "sub", "seed.txt"), "dirty inside the submodule\n");

        Finish(jobs, "job-1");
        return first;
    }

    [Fact]
    public async Task AFinishedCleanWorktreeWithAPopulatedSubmoduleGetsTheDedicatedMessageAndIsLeftInPlaceAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        JobWorktreeInfo first = await FinishedJobThatInitialisedTheSubmoduleAsync(repo, jobs, dirty: false);
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "worktrees", "job-1", "modules")), "the premise: a submodule initialised in a linked worktree keeps its git directory per worktree");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Equal(
            $"--branch claustrum/job-1: its previous worktree {first.Path} holds a populated submodule, which git will not remove — remove it by hand "
            + $"(`git worktree remove --force \"{first.Path}\"`, which also deletes any commit made inside the submodule and pushed nowhere) and retry",
            ex.Message);
        Assert.True(Directory.Exists(first.Path));
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-2")));
    }

    // The probe's whole point: a dirty worktree must NOT be told to `--force` — that would delete its uncommitted work.
    [Fact]
    public async Task AFinishedDirtyWorktreeWithAPopulatedSubmoduleGetsTheGenericLeftInPlaceTextAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        JobWorktreeInfo first = await FinishedJobThatInitialisedTheSubmoduleAsync(repo, jobs, dirty: true);

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("the worktree of finished job job-1, which was left in place (uncommitted changes:  M sub) — commit or discard its changes, then retry", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("populated submodule", ex.Message, StringComparison.Ordinal);
        Assert.Equal("dirty inside the submodule\n", File.ReadAllText(Path.Combine(first.Path, "sub", "seed.txt")));
    }

    // The probe's other half: a gitlink in the index whose directory holds a `.git`, with no per-worktree `modules` dir.
    [Fact]
    public async Task AGitlinkWhoseDirectoryHoldsAGitDirectoryCountsAsPopulatedEvenWithoutAModulesDirectoryAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string jobs = scratch.CreateDirectory();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(first.Path, "clone", "-q", repo, "vendor/y");
        GitRepo.Run(first.Path, "update-index", "--add", "--cacheinfo", $"160000,{GitRepo.Head(Path.Combine(first.Path, "vendor", "y"))},vendor/y");
        GitRepo.Run(first.Path, "commit", "-q", "-m", "a gitlink the role committed by hand");
        Assert.False(Directory.Exists(Path.Combine(repo, ".git", "worktrees", "job-1", "modules")));
        Finish(jobs, "job-1");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains($"its previous worktree {first.Path} holds a populated submodule", ex.Message, StringComparison.Ordinal);
    }

    // A false positive would refuse a freeable branch for good: the main checkout's `.git/modules` is not the worktree's,
    // and a gitlink whose directory is empty (uninitialised) is not populated.
    [Fact]
    public async Task AMainCheckoutWithModulesAndAnUninitialisedGitlinkInTheJobsWorktreeIsFreedNormallyAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "modules", "sub")), "the premise: the main checkout has the submodule's git directory");
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Assert.True(Directory.Exists(Path.Combine(first.Path, "sub")));
        Assert.False(Path.Exists(Path.Combine(first.Path, "sub", ".git")));
        File.WriteAllText(Path.Combine(first.Path, "real.txt"), "work\n");
        await JobWorktree.CommitRunAsync(first, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);
        Finish(jobs, "job-1");

        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
        Assert.Equal("work\n", File.ReadAllText(Path.Combine(second.Path, "real.txt")));
    }

    // The checks that come first are the ones that matter most: a running job's worktree is never offered for `--force`.
    [Fact]
    public async Task ARunningJobWithAPopulatedSubmoduleIsRefusedAsNotFinishedNotAsASubmoduleAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(first.Path, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q", "sub");
        Directory.CreateDirectory(Path.Combine(jobs, "job-1"));

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("which has not finished (no result.json)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(first.Path));
    }

    [Fact]
    public async Task AJobThisJobsRootDoesNotKnowWithAPopulatedSubmoduleIsRefusedAsUnknownAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        JobWorktreeInfo first = await JobWorktree.AddAsync(repo, "job-1", Ct);
        GitRepo.Run(first.Path, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q", "sub");

        BranchRefusedException ex = await Assert.ThrowsAsync<BranchRefusedException>(
            () => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        Assert.Contains("which is unknown under this CLAUSTRUM_HOME", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(first.Path));
    }

    // And the remedy the message names does what it says: removed by hand, the branch is free for the retry.
    [Fact]
    public async Task AfterTheSuggestedForceRemoveTheRetryChecksTheBranchOutAsync()
    {
        (string repo, string jobs) = RepoWithASubmodule();
        JobWorktreeInfo first = await FinishedJobThatInitialisedTheSubmoduleAsync(repo, jobs, dirty: false);
        await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct));

        GitRepo.Run(repo, "worktree", "remove", "--force", first.Path);
        JobWorktreeInfo second = await JobWorktree.CheckOutBranchAsync(repo, "job-2", "claustrum/job-1", jobs, Ct);

        Assert.Equal("work\n", File.ReadAllText(Path.Combine(second.Path, "real.txt")));
    }
}
