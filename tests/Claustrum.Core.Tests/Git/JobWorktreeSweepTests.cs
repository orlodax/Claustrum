using System.Runtime.Versioning;
using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #106 `jobs clean`'s sweep: deletes the `.trash-*` directories a free left under `.claustrum/worktrees`, after dropping the
// one admin entry each may still have, and never a repo-wide prune. A directory git still lists is somebody's worktree.
public sealed class JobWorktreeSweepTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-sweep-");
    private readonly List<string> lockedDirectories = [];

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in lockedDirectories.Where(Directory.Exists))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        scratch.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WorktreesRoot(string repo) => Path.Combine(repo, ".claustrum", "worktrees");

    private static string AdminOf(string worktree) => GitRepo.Run(worktree, "rev-parse", "--path-format=absolute", "--git-dir");

    private static string[] RegisteredNames(string repo) => [.. GitRepo.WorktreePaths(repo).Select(path => Path.GetFileName(path))];

    private static string MakeTrash(string repo, string name)
    {
        string trash = Directory.CreateDirectory(Path.Combine(WorktreesRoot(repo), name)).FullName;
        File.WriteAllText(Path.Combine(trash, "leftover.txt"), "x\n");
        return trash;
    }

    [Fact]
    public async Task TrashIsDeletedAndCountedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string first = MakeTrash(repo, ".trash-job-1-aaaaaaaa");
        string second = MakeTrash(repo, ".trash-job-2-bbbbbbbb");

        (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(2, swept);
        Assert.Empty(failures);
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public async Task NothingToSweepIsZeroWithOrWithoutAWorktreesDirectoryAsync()
    {
        string repo = scratch.CreateSeededRepo();

        Assert.Equal((0, []), await JobWorktree.SweepTrashAsync(repo, Ct));

        Directory.CreateDirectory(WorktreesRoot(repo));
        Assert.Equal((0, []), await JobWorktree.SweepTrashAsync(repo, Ct));
    }

    [Fact]
    public async Task AJobWorktreeBesideTheTrashIsNeverSweptAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo job = await JobWorktree.AddAsync(repo, "job-1", Ct);
        MakeTrash(repo, ".trash-job-9-cccccccc");

        (int swept, _) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(1, swept);
        Assert.True(Directory.Exists(job.Path));
        Assert.Contains("job-1", RegisteredNames(repo));
    }

    // A directory git lists as a worktree is not trash, whatever its name: it is somebody's, so it is reported and left.
    [Fact]
    public async Task ARegisteredTrashNamedWorktreeIsSkippedAndReportedAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string registered = Path.Combine(WorktreesRoot(repo), ".trash-x");
        Directory.CreateDirectory(WorktreesRoot(repo));
        GitRepo.Run(repo, "worktree", "add", "-q", "-b", "somebodys", registered);

        (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(0, swept);
        Assert.Contains("git lists it as a worktree", Assert.Single(failures), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(registered, "seed.txt")));
        Assert.Contains(".trash-x", RegisteredNames(repo));
    }

    // A free killed between its rename and the entry's deletion: the trash's `.git` names an entry whose top is gone. Only that
    // entry goes; an unrelated worktree whose directory is merely unmounted keeps its own.
    [Fact]
    public async Task ATrashWithADanglingEntryDropsOnlyItsOwnEntryAndAnUnrelatedMissingDirectoryEntryStaysAsync()
    {
        string repo = scratch.CreateSeededRepo();
        JobWorktreeInfo mine = await JobWorktree.AddAsync(repo, "job-1", Ct);
        string myAdmin = AdminOf(mine.Path);
        string unmounted = Path.Combine(scratch.CreateDirectory(), "unmounted-drive");
        GitRepo.Run(repo, "worktree", "add", "-q", "--detach", unmounted);
        string theirAdmin = AdminOf(unmounted);
        TempTree.Delete(unmounted);
        string trash = Path.Combine(WorktreesRoot(repo), ".trash-job-1-dddddddd");
        Directory.Move(mine.Path, trash);

        (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(repo, Ct);

        Assert.Equal(1, swept);
        Assert.Empty(failures);
        Assert.False(Directory.Exists(trash));
        Assert.False(Directory.Exists(myAdmin), "the trash's own entry should be dropped");
        Assert.True(Directory.Exists(theirAdmin), "an unrelated worktree's entry must not be pruned");
        Assert.Contains("unmounted-drive", RegisteredNames(repo));
    }

    [Fact]
    public async Task AFailingWorktreeListSweepsNothingAsync()
    {
        string notARepo = scratch.CreateDirectory();
        string trash = MakeTrash(notARepo, ".trash-job-1-eeeeeeee");

        (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(notARepo, Ct);

        Assert.Equal(0, swept);
        Assert.Contains("no trash was swept", Assert.Single(failures), StringComparison.Ordinal);
        Assert.True(Directory.Exists(trash));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UndeletableTrashIsAFailureNotAThrowAndIsNotCountedAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        string repo = scratch.CreateSeededRepo();
        string stuck = MakeTrash(repo, ".trash-job-1-ffffffff");
        string good = MakeTrash(repo, ".trash-job-2-11111111");
        lockedDirectories.Add(stuck);
        File.SetUnixFileMode(stuck, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (!TryWrite(stuck))
        {
            (int swept, string[] failures) = await JobWorktree.SweepTrashAsync(repo, Ct);

            Assert.Equal(1, swept);
            Assert.Contains(".trash-job-1-ffffffff", Assert.Single(failures), StringComparison.Ordinal);
            Assert.True(Directory.Exists(stuck));
            Assert.False(Directory.Exists(good));
        }
        else
        {
            Assert.Skip("this account is not stopped by a read-only directory (root, or a capability that bypasses modes).");
        }
    }

    private static bool TryWrite(string directory)
    {
        string probe = Path.Combine(directory, "probe");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ACancelledTokenStopsTheSweepAsync()
    {
        string repo = scratch.CreateSeededRepo();
        string trash = MakeTrash(repo, ".trash-job-1-22222222");
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.SweepTrashAsync(repo, cancelled.Token));

        Assert.True(Directory.Exists(trash));
    }
}
