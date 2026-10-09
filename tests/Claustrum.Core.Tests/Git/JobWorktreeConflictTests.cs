using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #74 rounds 4–5: the runner's `add -A` and `commit` CONCLUDE whatever the role left half-done — under MERGE_HEAD a merge
// commit, over `UU` entries conflict markers. So a worktree on its branch with a merge, a rebase or a `git am` in progress,
// or with ANY unmerged path, is the job's own worktree but not one the runner commits in: no commit, no delta, the
// snapshot kept, and a warning that sends the architect into the worktree (WorktreeMismatch.Operation / UnresolvedConflicts).
// A conflicted `merge --squash`, `stash pop`, `apply --3way` leaves no head of its own — only unmerged index entries — and a
// CLEAN `revert -n` / `cherry-pick -n`, or a cherry-pick whose conflict the role fixed and `git add`ed, is an ordinary
// commit (NOTES.md "Review round 5", J1 and J2). Every case below drives real git into the state it names.
public sealed class JobWorktreeConflictTests : IDisposable
{
    private const int Cap = 1_000_000;
    private const string Branch = "claustrum/job-1";

    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Two files, `f` and `g`, that branch `other` and the job's own commit both change (so any operation that brings `other`
    // in conflicts on two paths), and `clean-other`, which only adds `h.txt` (so one that brings it in does not).
    private async Task<(string Repo, JobWorktreeInfo Worktree)> StartAsync()
    {
        string repo = scratch.CreateSeededRepo();
        GitRepo.Commit(repo, "f", "base\n");
        GitRepo.Commit(repo, "g", "base\n");
        GitRepo.Run(repo, "checkout", "-q", "-b", "other");
        File.WriteAllText(Path.Combine(repo, "f"), "other\n");
        File.WriteAllText(Path.Combine(repo, "g"), "other\n");
        GitRepo.Run(repo, "commit", "-q", "-am", "other side");
        GitRepo.Run(repo, "checkout", "-q", "main");
        GitRepo.Run(repo, "checkout", "-q", "-b", "clean-other");
        GitRepo.Commit(repo, "h.txt", "added on the clean branch\n");
        GitRepo.Run(repo, "checkout", "-q", "main");

        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(worktree.Path, "f"), "job\n");
        File.WriteAllText(Path.Combine(worktree.Path, "g"), "job\n");
        GitRepo.Run(worktree.Path, "commit", "-q", "-am", "job work");
        return (repo, worktree);
    }

    private static void Edit(JobWorktreeInfo worktree, string file, string content) => File.WriteAllText(Path.Combine(worktree.Path, file), content);

    private static string Unmerged(JobWorktreeInfo worktree) => GitRepo.Run(worktree.Path, "ls-files", "-u");

    private static string Warning(string kind, JobWorktreeInfo worktree) => kind switch
    {
        "conflicts" => $"work left uncommitted: {worktree.Path} has unresolved conflicts on {Branch} — resolve or abort the operation inside the worktree, then commit there yourself",
        _ => $"work left uncommitted: {worktree.Path} has {kind} in progress, not a clean {Branch}",
    };

    // One row of the table: how to leave the worktree, then what VerifyAsync must say about it.
    public static TheoryData<string, string, string?, int> Stuck => new()
    {
        { "merge", "a merge is in progress", "a merge", 0 },
        { "merge-no-commit", "a merge is in progress", "a merge", 0 },
        { "git-am", "a git am is in progress", "a git am", 0 },
        { "rebase-merge-directory", "a rebase is in progress", "a rebase", 0 },
        { "rebase-apply-directory", "a rebase is in progress", "a rebase", 0 },
        { "merge-squash", "unresolved conflicts (2 paths)", null, 2 },
        { "stash-pop", "unresolved conflicts (2 paths)", null, 2 },
        { "stash-apply", "unresolved conflicts (2 paths)", null, 2 },
        { "apply-3way", "unresolved conflicts (1 path)", null, 1 },
        { "cherry-pick", "unresolved conflicts (2 paths)", null, 2 },
        { "revert", "unresolved conflicts (2 paths)", null, 2 },
    };

    private void LeaveStuck(string repo, JobWorktreeInfo worktree, string how)
    {
        string path = worktree.Path;
        switch (how)
        {
            case "merge":
                Assert.Equal(1, GitRepo.Try(path, "merge", "-q", "other").ExitCode);
                break;
            case "merge-no-commit":
                GitRepo.Run(path, "merge", "-q", "--no-commit", "--no-ff", "clean-other");
                break;
            case "git-am":
                string patch = Path.Combine(scratch.CreateDirectory(), "other.patch");
                File.WriteAllText(patch, GitRepo.Run(repo, "format-patch", "-1", "--stdout", "other") + "\n");
                Assert.NotEqual(0, GitRepo.Try(path, "am", "-3", patch).ExitCode);
                break;
            case "rebase-merge-directory":
                // A real stopped rebase detaches HEAD (the T3 case); a directory with HEAD still on the branch is the
                // shape the check defends against, made by hand.
                Directory.CreateDirectory(Path.GetFullPath(Path.Combine(path, GitRepo.Run(path, "rev-parse", "--git-path", "rebase-merge"))));
                break;
            case "rebase-apply-directory":
                Directory.CreateDirectory(Path.GetFullPath(Path.Combine(path, GitRepo.Run(path, "rev-parse", "--git-path", "rebase-apply"))));
                break;
            case "merge-squash":
                Assert.Equal(1, GitRepo.Try(path, "merge", "--squash", "other").ExitCode);
                break;
            case "stash-pop":
            case "stash-apply":
                Edit(worktree, "f", "stashed\n");
                Edit(worktree, "g", "stashed\n");
                GitRepo.Run(path, "stash", "push", "-q");
                Edit(worktree, "f", "after\n");
                Edit(worktree, "g", "after\n");
                GitRepo.Run(path, "commit", "-q", "-am", "after the stash");
                Assert.Equal(1, GitRepo.Try(path, "stash", how == "stash-pop" ? "pop" : "apply").ExitCode);
                break;
            case "apply-3way":
                string diff = Path.Combine(scratch.CreateDirectory(), "f.patch");
                File.WriteAllText(diff, GitRepo.Run(repo, "diff", "main~2", "other", "--", "f") + "\n");
                Assert.NotEqual(0, GitRepo.Try(path, "apply", "--3way", diff).ExitCode);
                break;
            case "cherry-pick":
                Assert.Equal(1, GitRepo.Try(path, "cherry-pick", "other").ExitCode);
                break;
            case "revert":
                Edit(worktree, "f", "later\n");
                Edit(worktree, "g", "later\n");
                GitRepo.Run(path, "commit", "-q", "-am", "later");
                Assert.Equal(1, GitRepo.Try(path, "revert", "--no-edit", "HEAD~1").ExitCode);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, "unknown way to leave a worktree stuck");
        }
    }

    [Theory]
    [MemberData(nameof(Stuck))]
    public async Task AWorktreeLeftInTheMiddleOfAnOperationIsTheJobsOwnAndNeverCommittedInAsync(string how, string reason, string? operation, int conflicts)
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        LeaveStuck(repo, worktree, how);
        Edit(worktree, "real.txt", "the role's other work\n");
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.NotEqual(worktree.BaseCommit, tip);
        SnapshotDiff snapshot = new([new ChangedFile("real.txt", ChangeKind.Added)], "snapshot diff", false);

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", snapshot, Cap, Ct);

        Assert.NotNull(mismatch);
        Assert.Equal(reason, mismatch.Reason);
        Assert.True(mismatch.OwnWorktree);
        Assert.Equal(operation, mismatch.Operation);
        Assert.Equal(conflicts, mismatch.UnresolvedConflicts);

        // Nothing concluded: no commit on the receipt even though the role committed before it stopped (the tip is the
        // role's own), the snapshot kept as read inside the worktree, and one warning that sends the architect in.
        Assert.Null(receipt.Commit);
        Assert.Same(snapshot, receipt.Changes);
        Assert.Equal([Warning(operation ?? "conflicts", worktree)], receipt.Warnings);
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Contains("?? real.txt", GitRepo.Status(worktree.Path), StringComparison.Ordinal);
        if (conflicts > 0)
            Assert.NotEqual("", Unmerged(worktree));
    }

    // J2: not a trigger — a clean `revert -n` leaves REVERT_HEAD and no unmerged path, and commits as an ordinary commit,
    // which clears the head (measured under the files and the reftable backends alike).
    [Fact]
    public async Task ACleanRevertWithNoCommitIsCommittedAsOneOrdinaryCommitAndTheHeadIsClearedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Edit(worktree, "added.txt", "added by the role\n");
        GitRepo.Run(worktree.Path, "add", "added.txt");
        GitRepo.Run(worktree.Path, "commit", "-q", "-m", "added.txt");
        GitRepo.Run(worktree.Path, "revert", "-n", "HEAD");
        Assert.Equal(0, GitRepo.Try(worktree.Path, "rev-parse", "-q", "--verify", "REVERT_HEAD").ExitCode);
        Edit(worktree, "real.txt", "work\n");

        Assert.Null(await JobWorktree.VerifyAsync(worktree, Ct));
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Equal(2, GitRepo.Run(repo, "rev-list", "--parents", "-n", "1", receipt.Commit).Split(' ').Length);
        Assert.Equal(["added.txt", "real.txt"], [.. GitRepo.Run(repo, "diff-tree", "-r", "--no-commit-id", "--name-only", receipt.Commit).Split('\n').Order(StringComparer.Ordinal)]);
        Assert.Equal(1, GitRepo.Try(worktree.Path, "rev-parse", "-q", "--verify", "REVERT_HEAD").ExitCode);
        Assert.Equal("", GitRepo.Status(worktree.Path));
    }

    [Fact]
    public async Task ACleanCherryPickWithNoCommitIsCommittedAsOneOrdinaryCommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "cherry-pick", "-n", "clean-other");
        Edit(worktree, "real.txt", "work\n");

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Equal(2, GitRepo.Run(repo, "rev-list", "--parents", "-n", "1", receipt.Commit).Split(' ').Length);
        Assert.Equal(["h.txt", "real.txt"], [.. GitRepo.Run(repo, "diff-tree", "-r", "--no-commit-id", "--name-only", receipt.Commit).Split('\n').Order(StringComparer.Ordinal)]);
        Assert.Equal("", GitRepo.Status(worktree.Path));
    }

    // A cherry-pick that stopped on a conflict, fixed by the role and `git add`ed: no unmerged path any more, the
    // CHERRY_PICK_HEAD still there — an ordinary single-parent commit, and the head goes with it.
    [Fact]
    public async Task ACherryPickWhoseConflictTheRoleFixedAndStagedIsCommittedAsOneOrdinaryCommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Assert.Equal(1, GitRepo.Try(worktree.Path, "cherry-pick", "other").ExitCode);
        Edit(worktree, "f", "fixed\n");
        Edit(worktree, "g", "fixed\n");
        GitRepo.Run(worktree.Path, "add", "f", "g");
        Assert.Equal(0, GitRepo.Try(worktree.Path, "rev-parse", "-q", "--verify", "CHERRY_PICK_HEAD").ExitCode);

        Assert.Null(await JobWorktree.VerifyAsync(worktree, Ct));
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        Assert.NotNull(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Equal(2, GitRepo.Run(repo, "rev-list", "--parents", "-n", "1", receipt.Commit).Split(' ').Length);
        Assert.Equal("fixed", GitRepo.Run(repo, "show", $"{receipt.Commit}:f"));
        Assert.Equal(1, GitRepo.Try(worktree.Path, "rev-parse", "-q", "--verify", "CHERRY_PICK_HEAD").ExitCode);
        Assert.Equal("", GitRepo.Status(worktree.Path));
    }

    // T3, for the real thing: a rebase stopped on a conflict DETACHES HEAD, so it is the off-branch case, not a head or a
    // directory check — the warning names the detached HEAD, and the conflicted files stay as the rebase left them.
    [Fact]
    public async Task ARebaseStoppedOnAConflictDetachesHeadAndIsTheOffBranchCaseNotACommitAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Assert.Equal(1, GitRepo.Try(worktree.Path, "rebase", "other").ExitCode);
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);
        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        Assert.NotNull(mismatch);
        Assert.Equal("a detached HEAD", mismatch.Reason);
        Assert.True(mismatch.OwnWorktree);
        Assert.Null(receipt.Commit);
        Assert.Equal([$"work left uncommitted: {worktree.Path} is on a detached HEAD, not {Branch}"], receipt.Warnings);
        Assert.Equal(tip, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.NotEqual("", Unmerged(worktree));
    }

    // One path or two: the count is of PATHS, not of index entries (a conflicted path has up to three stages).
    [Fact]
    public async Task TheConflictCountIsOfPathsNotOfIndexStagesAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        LeaveStuck(repo, worktree, "cherry-pick");
        Assert.True(Unmerged(worktree).Split('\n').Length >= 6, "two paths, up to three stages each");

        WorktreeMismatch? mismatch = await JobWorktree.VerifyAsync(worktree, Ct);

        Assert.Equal(2, mismatch?.UnresolvedConflicts);
        Assert.Equal("unresolved conflicts (2 paths)", mismatch?.Reason);
    }

    // The Unverified receipt for each shape, without a git: its text is the contract the architect's role routes on.
    [Fact]
    public void UnverifiedWordsAnOperationAndAnUnresolvedConflictCountForTheArchitect()
    {
        JobWorktreeInfo worktree = new("/repo/.claustrum/worktrees/j1", "claustrum/j1", "/repo", new string('a', 40), OwnsBranch: true);
        SnapshotDiff snapshot = new([new ChangedFile("a.txt", ChangeKind.Added)], "diff", false);

        BranchReceipt merge = JobWorktree.Unverified(worktree, new WorktreeMismatch("a merge is in progress", OwnWorktree: true, Operation: "a merge"), snapshot);
        BranchReceipt am = JobWorktree.Unverified(worktree, new WorktreeMismatch("a git am is in progress", OwnWorktree: true, Operation: "a git am"), snapshot);
        BranchReceipt conflicts = JobWorktree.Unverified(worktree, new WorktreeMismatch("unresolved conflicts (1 path)", OwnWorktree: true, UnresolvedConflicts: 1), snapshot);

        Assert.Equal(["work left uncommitted: /repo/.claustrum/worktrees/j1 has a merge in progress, not a clean claustrum/j1"], merge.Warnings);
        Assert.Equal(["work left uncommitted: /repo/.claustrum/worktrees/j1 has a git am in progress, not a clean claustrum/j1"], am.Warnings);
        Assert.Equal(["work left uncommitted: /repo/.claustrum/worktrees/j1 has unresolved conflicts on claustrum/j1 — resolve or abort the operation inside the worktree, then commit there yourself"], conflicts.Warnings);
        foreach (BranchReceipt receipt in new[] { merge, am, conflicts })
        {
            Assert.Null(receipt.Commit);
            Assert.Same(snapshot, receipt.Changes);
        }
    }
}
