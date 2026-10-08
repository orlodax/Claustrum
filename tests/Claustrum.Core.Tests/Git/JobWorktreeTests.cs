using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// Exercises JobWorktree against a real temporary git repo, the same way WorktreeSnapshotTests does. The
// scratch root deletes every repo this class makes (#47); the branch, undo, commit and remove halves of
// JobWorktree have their own classes beside this one.
public sealed class JobWorktreeTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose() => scratch.Dispose();

    [Fact]
    public async Task AddCreatesAWorktreeDirectoryOnANewBranchAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");

        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);

        Assert.True(Directory.Exists(info.Path));
        Assert.True(File.Exists(Path.Combine(info.Path, "seed.txt")));
        Assert.Equal("claustrum/job-1", info.Branch);
        Assert.Equal(Path.Combine(dir, ".claustrum", "worktrees", "job-1"), info.Path);
    }

    // F9: BaseCommit is where the branch starts, resolved before the add and handed to it — the sha the
    // receipt's delta and the abandoned-run cleanup both measure the branch against. OwnsBranch is what lets
    // that cleanup delete a branch the run created and spare one it was only given (#63).
    [Fact]
    public async Task AddRecordsTheStartingCommitTheMainCheckoutAndThatItOwnsTheBranchAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string seed = GitRepo.Commit(dir, "seed.txt", "seed\n");

        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);

        Assert.Equal(seed, info.BaseCommit);
        Assert.Equal(dir, info.MainCheckout);
        Assert.True(info.OwnsBranch);
        Assert.Equal(seed, GitRepo.RevParse(dir, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task AddOnARepositoryWithNoCommitSaysThereIsNothingToBranchFromAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(dir, "job-1", ct));

        Assert.Contains("no commit to branch from", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(JobWorktree.PathFor(dir, "job-1")));
    }

    [Fact]
    public async Task ChangesMadeInsideTheWorktreeAreInvisibleFromTheMainCheckoutAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");
        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);

        File.WriteAllText(Path.Combine(info.Path, "builder.txt"), "hi\n");

        Assert.False(File.Exists(Path.Combine(dir, "builder.txt")));
    }

    [Fact]
    public async Task RemoveDeletesTheWorktreeDirectoryButKeepsTheBranchAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");
        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);
        File.WriteAllText(Path.Combine(info.Path, "builder.txt"), "hi\n");
        GitRepo.Run(info.Path, "add", "-A");
        GitRepo.Run(info.Path, "commit", "-q", "-m", "builder work");

        await JobWorktree.RemoveAsync(dir, "job-1", ct);

        Assert.False(Directory.Exists(info.Path));
        Assert.Contains("claustrum/job-1", GitRepo.Branches(dir));
    }

    [Fact]
    public async Task TwoJobsGetTwoIndependentWorktreesAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");

        JobWorktreeInfo first = await JobWorktree.AddAsync(dir, "job-1", ct);
        JobWorktreeInfo second = await JobWorktree.AddAsync(dir, "job-2", ct);

        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(first.Branch, second.Branch);
        Assert.True(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    // R1: `--branch` runs on one branch take an exclusive lock under `.claustrum/locks/`, named by this key.
    [Fact]
    public void LockKeyForIsDeterministicAndKeepsTheBranchNameReadable()
    {
        Assert.Equal(JobWorktree.LockKeyFor("feature/x"), JobWorktree.LockKeyFor("feature/x"));
        Assert.StartsWith("branch@feature_x@", JobWorktree.LockKeyFor("feature/x"), StringComparison.Ordinal);
        Assert.NotEqual(JobWorktree.LockKeyFor("feature/x"), JobWorktree.LockKeyFor("feature/y"));
    }

    // `@` is turned into `_` by RoleConcurrencyGate.KeyFor, so no `<cast>__<role>` slot pool can ever be
    // the file a branch lock is.
    [Fact]
    public void LockKeyForContainsAnAtSignNoRoleSlotKeyCanContain()
    {
        string key = JobWorktree.LockKeyFor("claustrum/20261008-140548-3eb10efc");

        Assert.Contains('@', key);
        Assert.DoesNotContain('@', RoleConcurrencyGate.KeyFor("branch@x@y", "builder"));
    }

    // `a/b` and `a_b` sanitise to the same readable half; the hash of the exact name keeps them apart.
    [Fact]
    public void LockKeyForKeepsTwoBranchesThatSanitiseAlikeApart()
    {
        Assert.NotEqual(JobWorktree.LockKeyFor("a/b"), JobWorktree.LockKeyFor("a_b"));
        Assert.NotEqual(JobWorktree.LockKeyFor("a b"), JobWorktree.LockKeyFor("a:b"));
    }

    [Fact]
    public void LockKeyForABranchNameOf300CharactersIsBoundedAndPathSafe()
    {
        string branch = "release/" + new string('x', 292);

        string key = JobWorktree.LockKeyFor(branch);

        Assert.Equal(300, branch.Length);
        Assert.True(key.Length <= 80, $"key is {key.Length} characters");
        Assert.DoesNotContain(key, ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\');
        Assert.NotEqual(key, JobWorktree.LockKeyFor(branch + "y"));
    }

    // A name with a newline, a colon, a space and non-ASCII letters reaches the file system as '_' only.
    [Fact]
    public void LockKeyForReplacesEveryCharacterThatIsNotAsciiLetterDigitDashOrDot()
    {
        string key = JobWorktree.LockKeyFor("é:\n a/b.c-d");

        string readable = key.Split('@')[1];
        Assert.Equal("____a_b.c-d", readable);
    }
}
