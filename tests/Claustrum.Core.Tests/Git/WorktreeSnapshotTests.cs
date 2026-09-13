using System.Diagnostics;
using System.Text;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
// Alias to a DIFFERENT name, not `using Process = System.Diagnostics.Process;`: an alias directive
// is still just a using directive, so aliasing to the exact colliding simple name "Process" would
// lose to the enclosing-namespace member `Claustrum.Core.Process` the same way a plain using would
// (the same trap ConfigTests.cs documents for `Config`/`CoreConfig`). ProcessStartInfo has no
// colliding namespace member, so it needs no alias.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Core.Tests.Git;

// Exercises WorktreeSnapshot against a real temporary git repo (docs brief item 3's third bullet).
// Each test plays "before snapshot -> mutate worktree -> after snapshot -> diff" to mirror how
// Runner actually calls it around a backend spawn.
public sealed class WorktreeSnapshotTests
{
    [Fact]
    public async Task NewUntrackedFileIsAddedWithNoIndexDiffAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");

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
        string dir = CreateRepo();
        Commit(dir, "file.txt", "v1\n");
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
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");
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
        string dir = CreateRepo();
        Commit(dir, "old.txt", "identical content so similarity is 100%\n");

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(dir, ct);
        File.Move(Path.Combine(dir, "old.txt"), Path.Combine(dir, "new.txt"));
        RunGit(dir, "add", "-A"); // stage it so `git status` has a clear rename to detect
        WorktreeState after = await WorktreeSnapshot.CaptureAsync(dir, ct);

        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(dir, before, after, 1_000_000, ct);

        Assert.Equal(2, diff.ChangedFiles.Length);
        Assert.Contains(diff.ChangedFiles, f => f.Path == "new.txt" && f.Kind == ChangeKind.Added);
        Assert.Contains(diff.ChangedFiles, f => f.Path == "old.txt" && f.Kind == ChangeKind.Deleted);
    }

    [Fact]
    public async Task PreexistingDirtyFileLeftUntouchedIsExcludedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "file.txt", "v1\n");
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
        string dir = Directory.CreateTempSubdirectory("claustrum-nogit-").FullName;

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

    [Fact]
    public async Task TruncationBacksUpOverAMultibyteUtf8BoundaryAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");
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

    private static int FirstMultiByteSequenceStart(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            if ((bytes[i] & 0xC0) == 0xC0) // a UTF-8 lead byte (11xxxxxx), not a continuation byte
                return i;

        throw new InvalidOperationException("fixture diff has no multi-byte UTF-8 sequence");
    }

    private static string CreateRepo()
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-git-").FullName;
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "claustrum-tests");
        RunGit(dir, "config", "core.autocrlf", "false");
        return dir;
    }

    private static void Commit(string dir, string fileName, string content)
    {
        File.WriteAllText(Path.Combine(dir, fileName), content);
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "-q", "-m", $"add {fileName}");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using SystemProcess process = SystemProcess.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }
}
