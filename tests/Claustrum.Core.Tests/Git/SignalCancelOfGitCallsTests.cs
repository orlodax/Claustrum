using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// The signal-exit mapping lives in CommandProcess, so every git call made under a run's token is a cancel when git dies of the
// terminal's signal (a fake git exiting 129 = 128 + SIGHUP) and the token is cancelled shortly after - not a capture that falls
// back to a file scan, nor a "no commit to branch from" refusal. A script named `git` first on PATH plays that git for one call;
// the collection runs alone (PATH is process-wide). Unix only.
[Collection(FakeGitCollectionDefinition.Name)]
public sealed class SignalCancelOfGitCallsTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-signal-git-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASnapshotWhoseStatusDiesOfSighupAndWhoseTokenIsCancelledIsACancelNotAFileScanAsync()
    {
        string repo = scratch.CreateSeededRepo();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        using (new FakeGit("status", 129))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorktreeSnapshot.CaptureAsync(repo, cancel.Token));
    }

    [Fact]
    public async Task ASnapshotWhoseStatusDiesOfSighupWithNoCancelComingStillFallsBackToAFileScanAsync()
    {
        string repo = scratch.CreateSeededRepo();
        using CancellationTokenSource neverCancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        WorktreeState state;
        using (new FakeGit("status", 129))
            state = await WorktreeSnapshot.CaptureAsync(repo, neverCancelled.Token);

        Assert.IsType<WorktreeState.FileScan>(state);
    }

    [Fact]
    public async Task AddAsyncWhoseHeadLookupDiesOfSighupAndWhoseTokenIsCancelledIsACancelWithNothingLeftAsync()
    {
        string repo = scratch.CreateSeededRepo();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        using (new FakeGit("rev-parse", 129, alsoContains: "--verify"))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.AddAsync(repo, "job-1", cancel.Token));

        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
        Assert.DoesNotContain("claustrum/job-1", GitRepo.Branches(repo));
        Assert.Single(GitRepo.WorktreePaths(repo));
    }

    [Fact]
    public async Task AddAsyncWhoseHeadLookupDiesOfSighupWithNoCancelComingIsTheOrdinaryRefusalAsync()
    {
        string repo = scratch.CreateSeededRepo();
        using CancellationTokenSource neverCancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        using (new FakeGit("rev-parse", 129, alsoContains: "--verify"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, "job-1", neverCancelled.Token));

        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, "job-1")));
    }
}
