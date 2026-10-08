using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #61: after an isolated run the runner commits what the role left, on the job's own branch, with the repo's
// identity and hooks; the receipt's `commit` is the branch tip when the run moved it and its changes are the
// branch's delta from where the run started (F4). F6/R5/T3: only in the job's own worktree, on its branch.
// These drive JobWorktree.CommitRunAsync / VerifyAsync directly against real worktrees; the same behaviour
// through Runner is RunnerIsolationTests'.
public sealed class JobWorktreeCommitTests : IDisposable
{
    private const int Cap = 1_000_000;

    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Repo, JobWorktreeInfo Worktree)> StartAsync()
    {
        string repo = scratch.CreateSeededRepo();
        return (repo, await JobWorktree.AddAsync(repo, "job-1", Ct));
    }

    // What Runner hands CommitRunAsync: the before/after status snapshot around the role's work.
    private static async Task<SnapshotDiff> SnapshotAroundAsync(string worktreePath, Action work)
    {
        WorktreeState before = await WorktreeSnapshot.CaptureAsync(worktreePath, Ct);
        work();
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(worktreePath, Ct);
        return await WorktreeSnapshot.DiffAsync(worktreePath, before, after, Cap, Ct);
    }

    [Fact]
    public async Task LeftoversBecomeExactlyOneCommitOnTheBranchWithTheMessageAndACleanWorktreeAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            File.WriteAllText(Path.Combine(worktree.Path, "added.txt"), "new\n");
            File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "seed, edited\n");
        });

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1\n\nthe report summary", snapshot, Cap, Ct);

        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.Equal(tip, receipt.Commit);
        Assert.NotEqual(worktree.BaseCommit, tip);
        Assert.Equal(1, GitRepo.CommitCount(repo, $"{worktree.BaseCommit}..{tip}"));
        Assert.Equal("claustrum builder job-1", GitRepo.Run(repo, "log", "-1", "--format=%s", tip));
        Assert.Equal("the report summary", GitRepo.Run(repo, "log", "-1", "--format=%b", tip));
        Assert.Equal("", GitRepo.Status(worktree.Path));
        Assert.Empty(receipt.Warnings);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "added.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "seed.txt" && f.Kind == ChangeKind.Modified);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "main"));
    }

    [Fact]
    public async Task NothingChangedMeansNoCommitAndANullCommitOnTheReceiptAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = new([], null, false);

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Null(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Same(snapshot, receipt.Changes);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // A role that commits by itself moves HEAD, so `git status` shows nothing of it: the receipt names its
    // commit and lists what it changed, measured against where the run started.
    [Fact]
    public async Task ARoleThatCommittedItselfAndLeftNothingGetsItsOwnCommitRecordedWithItsDeltaAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            File.WriteAllText(Path.Combine(worktree.Path, "by-role.txt"), "committed by the role\n");
            GitRepo.Run(worktree.Path, "add", "-A");
            GitRepo.Run(worktree.Path, "commit", "-q", "-m", "role commit");
        });
        Assert.Empty(snapshot.ChangedFiles);

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.Equal(tip, receipt.Commit);
        Assert.Equal("role commit", GitRepo.Run(repo, "log", "-1", "--format=%s", tip));
        Assert.Equal(1, GitRepo.CommitCount(repo, $"{worktree.BaseCommit}..{tip}"));
        ChangedFile file = Assert.Single(receipt.Changes.ChangedFiles);
        Assert.Equal("by-role.txt", file.Path);
        Assert.Contains("+committed by the role", receipt.Changes.Diff, StringComparison.Ordinal);
        Assert.Empty(receipt.Warnings);
    }

    [Fact]
    public async Task ARoleCommitPlusLeftoversIsTwoCommitsAndTheReceiptNamesTheRunnersAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            File.WriteAllText(Path.Combine(worktree.Path, "first.txt"), "one\n");
            GitRepo.Run(worktree.Path, "add", "-A");
            GitRepo.Run(worktree.Path, "commit", "-q", "-m", "role commit");
            File.WriteAllText(Path.Combine(worktree.Path, "second.txt"), "two\n");
        });

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.Equal(tip, receipt.Commit);
        Assert.Equal("claustrum builder job-1", GitRepo.Run(repo, "log", "-1", "--format=%s", tip));
        Assert.Equal(2, GitRepo.CommitCount(repo, $"{worktree.BaseCommit}..{tip}"));
        Assert.Equal(["first.txt", "second.txt"], [.. receipt.Changes.ChangedFiles.Select(f => f.Path).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task ARenameLeftUncommittedIsReportedAsAnAddedAndADeletedPathAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Commit(repo, "old.txt", "identical content so similarity is 100%\n");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () => File.Move(Path.Combine(worktree.Path, "old.txt"), Path.Combine(worktree.Path, "new.txt")));

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.Equal(2, receipt.Changes.ChangedFiles.Length);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "new.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "old.txt" && f.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public async Task TheDeltaDiffIsTruncatedAtTheByteCapAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
            File.WriteAllText(Path.Combine(worktree.Path, "big.txt"), string.Concat(Enumerable.Repeat("a line that will not fit in the cap\n", 50))));

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, 200, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.True(receipt.Changes.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(receipt.Changes.Diff ?? "") <= 200);
        Assert.Single(receipt.Changes.ChangedFiles);
    }

    // The commit git refuses costs a warning, never the run: the hook's first line is the reason.
    [Fact]
    public async Task ACommitThatAPreCommitHookRefusesLeavesTheWorkInPlaceWithAWarningAndNoCommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.WriteHook(repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () => File.WriteAllText(Path.Combine(worktree.Path, "wanted.txt"), "work\n"));

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Null(receipt.Commit);
        Assert.Equal(["work left uncommitted on claustrum/job-1: hook says no"], receipt.Warnings);
        Assert.Same(snapshot, receipt.Changes);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal("A  wanted.txt", GitRepo.Status(worktree.Path));
    }

    // "A refused commit after the role had committed some work itself reports the role's commit and the
    // warning": the tip the role made is real. The runner's `git add -A` had already staged the file the hook
    // then refused, so `git diff <base>` sees it and it is in the delta, text included.
    [Fact]
    public async Task ARoleCommitFollowedByARefusedRunnerCommitReportsBothTheCommitAndTheWarningAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.WriteHook(repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            File.WriteAllText(Path.Combine(worktree.Path, "by-role.txt"), "committed by the role\n");
            GitRepo.Run(worktree.Path, "add", "-A");
            GitRepo.Run(worktree.Path, "commit", "-q", "--no-verify", "-m", "role commit");
            File.WriteAllText(Path.Combine(worktree.Path, "left.txt"), "left behind\n");
        });

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), receipt.Commit);
        Assert.Equal(["work left uncommitted on claustrum/job-1: hook says no"], receipt.Warnings);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "by-role.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(receipt.Changes.ChangedFiles, f => f.Path == "left.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains("+left behind", receipt.Changes.Diff, StringComparison.Ordinal);
        Assert.Contains("+committed by the role", receipt.Changes.Diff, StringComparison.Ordinal);
        Assert.Equal("A  left.txt", GitRepo.Status(worktree.Path));
    }

    // R4: a delta git cannot produce, after the commit was made, must not turn that commit into "left
    // uncommitted". A BaseCommit git has never heard of makes the diff fail.
    [Fact]
    public async Task ADeltaThatFailsAfterACommitKeepsTheCommitAndTheSnapshotAndWarnsAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        JobWorktreeInfo bogusBase = worktree with { BaseCommit = new string('0', 40) };
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () => File.WriteAllText(Path.Combine(worktree.Path, "added.txt"), "new\n"));

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(bogusBase, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), receipt.Commit);
        Assert.Same(snapshot, receipt.Changes);
        string warning = Assert.Single(receipt.Warnings);
        Assert.StartsWith("receipt delta unavailable, snapshot kept: ", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("work left uncommitted", warning, StringComparison.Ordinal);
        Assert.Equal("", GitRepo.Status(worktree.Path));
    }

    // R2: `status.showUntrackedFiles=no` makes a plain `git status --porcelain` blind to a role that only
    // created files, so the commit step never ran and the receipt said `commit: null`. Every status here
    // passes --untracked-files=all.
    [Fact]
    public async Task ARoleThatOnlyCreatesAFileIsStillCommittedWhenStatusHidesUntrackedFilesAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(repo, "config", "status.showUntrackedFiles", "no");
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () => File.WriteAllText(Path.Combine(worktree.Path, "only-new.txt"), "new\n"));
        Assert.Equal("", GitRepo.Run(worktree.Path, "status", "--porcelain"));

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.Equal("only-new.txt", GitRepo.Run(repo, "show", "--name-only", "--format=", receipt.Commit));
        Assert.Empty(receipt.Warnings);
    }

    [Fact]
    public async Task AWorktreeOnItsBranchVerifiesAsNullAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync();

        Assert.Null(await JobWorktree.VerifyAsync(worktree, Ct));
    }

    [Fact]
    public async Task ADetachedHeadIsTheJobsOwnWorktreeOffItsBranchAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);

        Assert.NotNull(mismatch);
        Assert.Equal("a detached HEAD", mismatch.Reason);
        Assert.True(mismatch.OwnWorktree);
    }

    [Fact]
    public async Task ASwitchToAnotherBranchIsTheJobsOwnWorktreeOffItsBranchAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "switch", "-q", "-c", "other");

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);

        Assert.NotNull(mismatch);
        Assert.Equal("refs/heads/other", mismatch.Reason);
        Assert.True(mismatch.OwnWorktree);
    }

    // T3: a role stopped mid-rebase (detached HEAD) or on another branch keeps the snapshot that was read
    // inside its own worktree, gets no runner commit — it would land off the branch — and a warning that
    // sends the architect into the worktree.
    [Fact]
    public async Task ARunLeftOnADetachedHeadKeepsItsSnapshotGetsNoCommitAndAWarningAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");
            File.WriteAllText(Path.Combine(worktree.Path, "left.txt"), "left behind\n");
        });
        int commitsBefore = GitRepo.CommitCount(repo, "--all");

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Null(receipt.Commit);
        Assert.Same(snapshot, receipt.Changes);
        Assert.Equal([$"work left uncommitted: {worktree.Path} is on a detached HEAD, not claustrum/job-1"], receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal(commitsBefore, GitRepo.CommitCount(repo, "--all"));
        Assert.Equal("?? left.txt", GitRepo.Status(worktree.Path));
    }

    [Fact]
    public async Task ARunLeftOnAnotherBranchKeepsItsSnapshotGetsNoCommitAndNamesTheBranchAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        SnapshotDiff snapshot = await SnapshotAroundAsync(worktree.Path, () =>
        {
            GitRepo.Run(worktree.Path, "switch", "-q", "-c", "other");
            File.WriteAllText(Path.Combine(worktree.Path, "left.txt"), "left behind\n");
        });

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.Null(receipt.Commit);
        Assert.Same(snapshot, receipt.Changes);
        Assert.Equal([$"work left uncommitted: {worktree.Path} is on refs/heads/other, not claustrum/job-1"], receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/other"));
        Assert.Equal("?? left.txt", GitRepo.Status(worktree.Path));
    }

    // F6: the worktree was removed while the job ran and the role's next write recreated its path as a plain
    // directory inside the main checkout. git resolves it to the main checkout, where `git add -A` used to
    // commit the operator's own dirty files onto the operator's branch (reproduced in review). R5: nor does
    // the receipt carry them as the run's changes.
    [Fact]
    public async Task ARecreatedPlainDirectoryNextToADirtyMainCheckoutCommitsNothingAndLeavesTheMainCheckoutUntouchedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(repo, "worktree", "remove", "--force", worktree.Path);
        Directory.CreateDirectory(worktree.Path);
        File.WriteAllText(Path.Combine(worktree.Path, "lost.txt"), "written after the worktree was removed\n");
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "the operator's own edit\n");
        File.WriteAllText(Path.Combine(repo, "mine.txt"), "the operator's own new file\n");
        string mainHead = GitRepo.Head(repo);
        string mainStatus = GitRepo.Status(repo);

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(
            worktree, "claustrum builder job-1", new SnapshotDiff([new ChangedFile("seed.txt", ChangeKind.Modified)], "operator's diff", false), Cap, Ct);

        Assert.NotNull(mismatch);
        Assert.False(mismatch.OwnWorktree);
        Assert.Contains("inside another checkout", mismatch.Reason, StringComparison.Ordinal);
        Assert.Null(receipt.Commit);
        Assert.Empty(receipt.Changes.ChangedFiles);
        Assert.Null(receipt.Changes.Diff);
        string warning = Assert.Single(receipt.Warnings);
        Assert.StartsWith($"work left uncommitted: {worktree.Path} is not the job worktree on claustrum/job-1 (", warning, StringComparison.Ordinal);
        Assert.Equal(mainHead, GitRepo.Head(repo));
        Assert.Equal(mainStatus, GitRepo.Status(repo));
        Assert.Equal("the operator's own edit\n", File.ReadAllText(Path.Combine(repo, "seed.txt")));
        Assert.Equal("the operator's own new file\n", File.ReadAllText(Path.Combine(repo, "mine.txt")));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task ADirectoryThatIsGoneIsReportedAsSuchAndCommitsNothingAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(repo, "worktree", "remove", "--force", worktree.Path);

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        Assert.NotNull(mismatch);
        Assert.Equal("the directory is gone", mismatch.Reason);
        Assert.False(mismatch.OwnWorktree);
        Assert.Null(receipt.Commit);
        Assert.Equal([$"work left uncommitted: {worktree.Path} is not the job worktree on claustrum/job-1 (the directory is gone)"], receipt.Warnings);
    }

    [Fact]
    public void UnverifiedKeepsTheSnapshotOnlyForTheJobsOwnWorktree()
    {
        JobWorktreeInfo worktree = new("/repo/.claustrum/worktrees/j1", "claustrum/j1", "/repo", new string('a', 40), OwnsBranch: true);
        SnapshotDiff snapshot = new([new ChangedFile("a.txt", ChangeKind.Added)], "diff", false);

        BranchReceipt own = JobWorktree.Unverified(worktree, new WorktreeMismatch("a detached HEAD", OwnWorktree: true), snapshot);
        BranchReceipt foreign = JobWorktree.Unverified(worktree, new WorktreeMismatch("git resolves it to x/ inside another checkout", OwnWorktree: false), snapshot);

        Assert.Same(snapshot, own.Changes);
        Assert.Null(own.Commit);
        Assert.Empty(foreign.Changes.ChangedFiles);
        Assert.Null(foreign.Changes.Diff);
        Assert.Null(foreign.Commit);
    }
}
