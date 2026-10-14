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
}
