using System.Runtime.Versioning;
using System.Text;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// Exercises WorktreeSnapshot against a real temporary git repo (docs brief item 3's third bullet).
// Each test plays "before snapshot -> mutate worktree -> after snapshot -> diff" to mirror how
// Runner actually calls it around a backend spawn.
public sealed class WorktreeSnapshotTests : IDisposable
{
    // #47: this class left a `claustrum-git-*` and a `claustrum-nogit-*` directory behind per test. Each
    // root deletes what it made when the class is disposed; the two prefixes are kept so a stray one still
    // names the test that leaked it.
    private readonly ScratchRoot scratch = new("claustrum-git-");
    private readonly ScratchRoot nogit = new("claustrum-nogit-");

    public void Dispose()
    {
        scratch.Dispose();
        nogit.Dispose();
    }

    [Fact]
    public async Task NewUntrackedFileIsAddedWithNoIndexDiffAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hi\n");
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        ChangedFile file = Assert.Single(diff.ChangedFiles);
        Assert.Equal("hello.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);
        Assert.Contains("hi", diff.Diff);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task AlreadyDirtyFileEditedAgainIsStillReportedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "file.txt", "v1\n");
        File.WriteAllText(Path.Combine(dir, "file.txt"), "v2\n"); // dirty before the "run" starts

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "v3\n"); // edited again during the "run"
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        ChangedFile file = Assert.Single(diff.ChangedFiles);
        Assert.Equal("file.txt", file.Path);
        Assert.Equal(ChangeKind.Modified, file.Kind);
    }

    [Fact]
    public async Task UntrackedFileEditedAgainKeepsAddedKindAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");
        File.WriteAllText(Path.Combine(dir, "new.txt"), "v1\n"); // untracked before the "run"

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "new.txt"), "v2\n"); // edited again, still untracked
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        ChangedFile file = Assert.Single(diff.ChangedFiles);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);
    }

    [Fact]
    public async Task RenameReportsNewPathAddedAndOldPathDeletedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "old.txt", "identical content so similarity is 100%\n");

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.Move(Path.Combine(dir, "old.txt"), Path.Combine(dir, "new.txt"));
        GitRepo.Run(dir, "add", "-A"); // stage it so `git status` has a clear rename to detect
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        Assert.Equal(2, diff.ChangedFiles.Length);
        Assert.Contains(diff.ChangedFiles, f => f.Path == "new.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(diff.ChangedFiles, f => f.Path == "old.txt" && f.Kind == ChangeKind.Deleted);
    }

    // 2026-09-14 (issue #3): a locked/unreadable file used to make HashWorktreeFile's bare
    // File.OpenRead throw, and that exception came straight out of CaptureAsync with nothing in this
    // class to catch it. Windows needs FileShare.None (an open editor/AV/OneDrive lock); a non-root
    // Linux process cannot lock a file against itself that way, so it uses chmod 000 instead — both
    // leave the file present but unreadable, which is the shape HashWorktreeFile must now tolerate.
    [Fact]
    public async Task LockedFileIsUnhashableInsteadOfFatalAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "file.txt", "v1\n");
        string path = Path.Combine(dir, "file.txt");
        File.WriteAllText(path, "v2\n"); // dirty before the "run" starts

        using (MakeFileUnreadable(path))
        {
            WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);

            GitStatusEntry entry = Assert.Single(((WorktreeState.Git)before).StatusEntries);
            Assert.Equal("file.txt", entry.Path);
            Assert.Null(entry.ContentHash);
        }
    }

    private static IDisposable MakeFileUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        return new UnixUnreadableFile(path);
    }

    // File.OpenRead on a 000-mode file throws UnauthorizedAccessException for any non-root process —
    // exactly the branch HashWorktreeFile's catch now covers. Restores the original mode on Dispose
    // so CreateRepo's temp directory can still be deleted afterwards.
    [UnsupportedOSPlatform("windows")]
    private sealed class UnixUnreadableFile : IDisposable
    {
        private readonly string path;
        private readonly UnixFileMode original;

        public UnixUnreadableFile(string path)
        {
            this.path = path;
            original = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        public void Dispose() => File.SetUnixFileMode(path, original);
    }

    [Fact]
    public async Task PreexistingDirtyFileLeftUntouchedIsExcludedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "file.txt", "v1\n");
        File.WriteAllText(Path.Combine(dir, "file.txt"), "v2\n"); // dirty before the "run" starts

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        // no further edits during the "run"
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        Assert.Empty(diff.ChangedFiles);
        Assert.Null(diff.Diff);
    }

    [Fact]
    public async Task NonGitDirectoryFallsBackToFileScanWithNullDiffAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = nogit.CreateDirectory();

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        Assert.IsType<WorktreeState.FileScan>(before);

        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hi\n");
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        ChangedFile file = Assert.Single(diff.ChangedFiles);
        Assert.Equal("hello.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);
        Assert.Null(diff.Diff);
    }

    // 2026-09-21 (issue #12): the non-git ScanFiles fallback used SearchOption.AllDirectories, so one
    // unreadable subdirectory threw UnauthorizedAccessException out of CaptureAsync and turned an
    // already-finished run into RunStatus.Failed (doctor --probe's own temp cwd, in practice). It now
    // walks by hand and skips what it cannot read, the same trade LockedFileIsUnhashableInsteadOfFatalAsync
    // proves for the git branch's HashWorktreeFile.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnreadableSubdirectoryIsSkippedNotFatalInTheFileScanFallbackAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("chmod 000 does not deny a Windows directory listing the same way, and nothing here can inject an equivalent lock without the path in hand.");
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = nogit.CreateDirectory();
        string locked = Path.Combine(dir, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "unreachable.txt"), "never seen\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            File.WriteAllText(Path.Combine(dir, "readable.txt"), "v1\n");
            File.WriteAllText(Path.Combine(dir, ".hidden"), "hidden\n");

            WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
            WorktreeState.FileScan beforeScan = Assert.IsType<WorktreeState.FileScan>(before);
            Assert.Contains("readable.txt", beforeScan.Files.Keys);
            Assert.Contains(".hidden", beforeScan.Files.Keys);
            Assert.DoesNotContain(beforeScan.Files.Keys, path => path.Contains("locked", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(dir, "readable.txt"), "v2\n"); // the one change between the two captures
            WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

            SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

            ChangedFile file = Assert.Single(diff.ChangedFiles);
            Assert.Equal("readable.txt", file.Path);
            Assert.Equal(ChangeKind.Modified, file.Kind);
        }
        finally
        {
            // The owner can always chmod their own directory back regardless of its current mode —
            // needed so the scratch root can delete the tree at all.
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task TruncationBacksUpOverAMultibyteUtf8BoundaryAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");
        string content = new string('€', 50) + "\n"; // U+20AC, 3 bytes in UTF-8

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "money.txt"), content);
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff full = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);
        string fullDiff = full.Diff ?? throw new InvalidOperationException("expected a non-null diff for a new untracked file");
        byte[] fullBytes = Encoding.UTF8.GetBytes(fullDiff);
        int cap = FirstMultiByteSequenceStart(fullBytes) + 1; // lands one byte into a 3-byte sequence

        SnapshotDiff truncated = await WorktreeSnapshot.DiffAsync(dir, before, after, cap, ct);
        string truncatedDiff = truncated.Diff ?? throw new InvalidOperationException("expected a non-null truncated diff");

        Assert.True(truncated.Truncated);
        Assert.DoesNotContain('�', truncatedDiff);
        Assert.True(Encoding.UTF8.GetByteCount(truncatedDiff) <= cap);
    }

    // F4: an isolated run's receipt is the branch's delta from the commit it started on, because a role
    // that commits by itself moves HEAD and `git status` / `git diff HEAD` then show nothing of its work.
    [Fact]
    public async Task BranchDeltaListsWhatWasCommittedSinceTheStartingCommitAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        GitRepo.Commit(dir, "committed.txt", "by the role\n");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct);

        Assert.NotNull(delta);
        ChangedFile file = Assert.Single(delta.ChangedFiles);
        Assert.Equal("committed.txt", file.Path);
        Assert.Equal(ChangeKind.Added, file.Kind);
        Assert.Contains("+by the role", delta.Diff, StringComparison.Ordinal);
        Assert.False(delta.Truncated);
    }

    // --no-renames: git itself splits a rename into the A + D pair the status snapshot produces.
    [Fact]
    public async Task BranchDeltaReportsARenameAsAnAddedAndADeletedPathAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "old.txt", "identical content so similarity is 100%\n");
        GitRepo.Run(dir, "mv", "old.txt", "new.txt");
        GitRepo.Run(dir, "commit", "-q", "-m", "rename");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct);

        Assert.NotNull(delta);
        Assert.Equal(2, delta.ChangedFiles.Length);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "new.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "old.txt" && f.Kind == ChangeKind.Deleted);
    }

    // `git diff <commit>` sees the index only. A new file the runner's commit could not take stays
    // untracked, and the snapshot is what knows the run made it: it gets the `--no-index` text.
    [Fact]
    public async Task BranchDeltaAddsAStillUntrackedSnapshotFileWithItsNoIndexTextAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        GitRepo.Commit(dir, "committed.txt", "by the role\n");
        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "left.txt"), "hello left\n");
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);
        SnapshotDiff snapshot = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, snapshot, 1_000_000, ct);

        Assert.NotNull(delta);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "committed.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "left.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains("+hello left", delta.Diff, StringComparison.Ordinal);
        Assert.Contains("+by the role", delta.Diff, StringComparison.Ordinal);
    }

    // The snapshot's Added file that a commit has since taken is listed once, from the commit, not twice.
    [Fact]
    public async Task BranchDeltaListsAFileOnceWhenTheSnapshotSawItUntrackedAndACommitTookItAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.WriteAllText(Path.Combine(dir, "new.txt"), "new\n");
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);
        SnapshotDiff snapshot = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);
        GitRepo.Run(dir, "add", "-A");
        GitRepo.Run(dir, "commit", "-q", "-m", "runner commit");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, snapshot, 1_000_000, ct);

        Assert.NotNull(delta);
        Assert.Single(delta.ChangedFiles);
        Assert.Equal(1, CountOf(delta.Diff, "+++ b/new.txt"));
    }

    // Uncommitted tracked edits are measured against the same starting commit as the committed ones.
    [Fact]
    public async Task BranchDeltaIncludesATrackedEditThatIsStillUncommittedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        GitRepo.Commit(dir, "committed.txt", "by the role\n");
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed, edited\n");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct);

        Assert.NotNull(delta);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "seed.txt" && f.Kind == ChangeKind.Modified);
        Assert.Contains(delta.ChangedFiles, f => f.Path == "committed.txt" && f.Kind == ChangeKind.Added);
    }

    // Added, committed, then deleted again: git lists nothing for it, and neither do we.
    [Fact]
    public async Task BranchDeltaOfAFileAddedAndRemovedAgainIsEmptyAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        GitRepo.Commit(dir, "scratch.txt", "temporary\n");
        GitRepo.Run(dir, "rm", "-q", "scratch.txt");
        GitRepo.Run(dir, "commit", "-q", "-m", "remove it again");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 1_000_000, ct);

        Assert.NotNull(delta);
        Assert.Empty(delta.ChangedFiles);
        Assert.Null(delta.Diff);
    }

    [Fact]
    public async Task BranchDeltaAppliesTheByteCapAndMarksTheDiffTruncatedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        string start = GitRepo.Commit(dir, "seed.txt", "seed\n");
        GitRepo.Commit(dir, "big.txt", string.Concat(Enumerable.Repeat("a line that will not fit in the cap\n", 50)));

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, start, new SnapshotDiff([], null, false), 200, ct);

        Assert.NotNull(delta);
        Assert.True(delta.Truncated);
        Assert.NotNull(delta.Diff);
        Assert.True(Encoding.UTF8.GetByteCount(delta.Diff) <= 200);
        Assert.Single(delta.ChangedFiles);
    }

    // Null is "git cannot say": the caller keeps its snapshot and warns (receipt delta unavailable).
    [Fact]
    public async Task BranchDeltaAgainstACommitGitDoesNotKnowIsNullAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = scratch.CreateRepo();
        GitRepo.Commit(dir, "seed.txt", "seed\n");

        SnapshotDiff? delta = await WorktreeSnapshot.BranchDeltaAsync(dir, new string('0', 40), new SnapshotDiff([], null, false), 1_000_000, ct);

        Assert.Null(delta);
    }

    private static int CountOf(string? text, string needle) => text is null ? 0 : text.Split(needle).Length - 1;

    private static int FirstMultiByteSequenceStart(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            if ((bytes[i] & 0xC0) == 0xC0) // a UTF-8 lead byte (11xxxxxx), not a continuation byte
                return i;

        throw new InvalidOperationException("fixture diff has no multi-byte UTF-8 sequence");
    }
}
