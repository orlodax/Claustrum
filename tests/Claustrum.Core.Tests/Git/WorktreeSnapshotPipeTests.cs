using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// 2026-10-13 (found by review): a tracked file replaced by a FIFO passes File.Exists, and hashing it blocked in open(2) until a
// writer appeared, so the snapshot a Runner takes before and after a backend spawn hung. Unix only (mkfifo).
public sealed class WorktreeSnapshotPipeTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-snap-pipe-");

    public void Dispose() => scratch.Dispose();

    private string RepoWithTrackedFileReplacedByAFifo(string file)
    {
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, file, "tracked\n");
        File.Delete(Path.Combine(dir, file));
        PipeFiles.MakeFifo(Path.Combine(dir, file));
        return dir;
    }

    [Fact]
    public async Task ATrackedFileReplacedByAFifoIsCapturedPromptlyWithNoContentHashAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = RepoWithTrackedFileReplacedByAFifo("tracked.txt");

        WorktreeState state = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));

        WorktreeState.Git git = Assert.IsType<WorktreeState.Git>(state);
        GitStatusEntry entry = Assert.Single(git.StatusEntries, e => e.Path == "tracked.txt");
        Assert.Null(entry.ContentHash);
    }

    [Fact]
    public async Task ABeforeAndAfterCaptureAroundAFifoDoNotHangAndReportNoPhantomChangeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = RepoWithTrackedFileReplacedByAFifo("tracked.txt");

        WorktreeState before = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));
        File.WriteAllText(Path.Combine(dir, "new.txt"), "x\n");
        WorktreeState after = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));
        SnapshotDiff diff = await PipeFiles.WithinAsync(() => WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct));

        Assert.Equal(["new.txt"], diff.ChangedFiles.Select(file => file.Path));
    }

    // ---- a tracked link is hashed as text, a FIFO beside a modified sibling costs only its own diff (2026-10-14) ------------

    [Fact]
    public async Task ATrackedFileReplacedByALinkToDevZeroIsCapturedPromptlyAndHashedAsItsTargetTextAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/zero is a Unix device.");

        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "tracked.txt", "tracked\n");
        File.Delete(Path.Combine(dir, "tracked.txt"));
        File.CreateSymbolicLink(Path.Combine(dir, "tracked.txt"), "/dev/zero");

        WorktreeState state = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));

        GitStatusEntry entry = Assert.Single(Assert.IsType<WorktreeState.Git>(state).StatusEntries, e => e.Path == "tracked.txt");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("/dev/zero"u8)), entry.ContentHash);
    }

    [Fact]
    public async Task ALinkToADirectoryOrADanglingLinkInPlaceOfATrackedFileIsHashableAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "a.txt", "a\n");
        GitRepo.Commit(dir, "b.txt", "b\n");
        File.Delete(Path.Combine(dir, "a.txt"));
        File.Delete(Path.Combine(dir, "b.txt"));
        Directory.CreateDirectory(Path.Combine(dir, "somewhere"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "a.txt"), "somewhere");
            File.CreateSymbolicLink(Path.Combine(dir, "b.txt"), "not-there");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }

        WorktreeState state = await WorktreeSnapshot.CaptureAsync(dir, ct);

        WorktreeState.Git git = Assert.IsType<WorktreeState.Git>(state);
        Assert.NotNull(Assert.Single(git.StatusEntries, e => e.Path == "a.txt").ContentHash);
        Assert.NotNull(Assert.Single(git.StatusEntries, e => e.Path == "b.txt").ContentHash);
    }

    [Fact]
    public async Task AFifoBesideAModifiedSiblingListsBothButDiffsOnlyTheSiblingAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "pipe.txt", "was a file\n");
        GitRepo.Commit(dir, "sibling.txt", "before\n");
        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.Delete(Path.Combine(dir, "pipe.txt"));
        PipeFiles.MakeFifo(Path.Combine(dir, "pipe.txt"));
        File.WriteAllText(Path.Combine(dir, "sibling.txt"), "after\n");
        WorktreeState after = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));

        SnapshotDiff diff = await PipeFiles.WithinAsync(() => WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct));

        Assert.Equal(["pipe.txt", "sibling.txt"], diff.ChangedFiles.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Contains("+after", diff.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe.txt", diff.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBranchDeltaWithAFifoBesideAModifiedSiblingNamesBothAndDiffsOnlyTheSiblingAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "pipe.txt", "was a file\n");
        string start = GitRepo.Commit(dir, "sibling.txt", "before\n");
        File.Delete(Path.Combine(dir, "pipe.txt"));
        PipeFiles.MakeFifo(Path.Combine(dir, "pipe.txt"));
        File.WriteAllText(Path.Combine(dir, "sibling.txt"), "after\n");

        SnapshotDiff? delta = await PipeFiles.WithinAsync(() => WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct));

        Assert.NotNull(delta);
        Assert.Contains("pipe.txt", delta.ChangedFiles.Select(file => file.Path));
        Assert.Contains("sibling.txt", delta.ChangedFiles.Select(file => file.Path));
        Assert.Contains("+after", delta.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe.txt", delta.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntrackedFifoIsIgnoredByCaptureAndByTheBranchDeltaAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        PipeFiles.MakeFifo(Path.Combine(dir, "stray-pipe"));

        WorktreeState state = await PipeFiles.WithinAsync(() => WorktreeSnapshot.CaptureAsync(dir, ct));
        SnapshotDiff? delta = await PipeFiles.WithinAsync(() => WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct));

        Assert.DoesNotContain(Assert.IsType<WorktreeState.Git>(state).StatusEntries, e => e.Path == "stray-pipe");
        Assert.NotNull(delta);
        Assert.Empty(delta.ChangedFiles);
    }
}
