namespace Claustrum.Core.Tests.Testing;

// #47: JobWorktreeTests and WorktreeSnapshotTests left a `claustrum-git-*`, `claustrum-worktree-*` and
// `claustrum-nogit-*` directory in the machine's temp directory per test (84, 48 and 12 found in /tmp). The
// scratch root every git fixture now owns is what removes them; this proves it does, read-only git objects
// and registered worktrees included, under a prefix nothing else in the suite uses so a concurrent test
// class cannot make the count racy.
public sealed class ScratchRootTests
{
    private static string[] Leftovers(string prefix) =>
        Directory.GetDirectories(Path.GetTempPath(), $"{prefix}*");

    [Fact]
    public void DisposeDeletesEveryRepositoryAndWorktreeTheRootCreated()
    {
        string prefix = $"claustrum-scratch-{Guid.NewGuid():N}-";
        ScratchRoot scratch = new(prefix);
        string repo = scratch.CreateSeededRepo();
        string plain = scratch.CreateDirectory();
        string worktree = Path.Combine(repo, ".claustrum", "worktrees", "job-1");
        GitRepo.Run(repo, "worktree", "add", "-q", worktree, "-b", "claustrum/job-1");
        File.WriteAllText(Path.Combine(worktree, "work.txt"), "work\n");
        GitRepo.Run(worktree, "add", "-A");
        GitRepo.Run(worktree, "commit", "-q", "-m", "work");
        Assert.Equal(2, Leftovers(prefix).Length);

        scratch.Dispose();

        Assert.Empty(Leftovers(prefix));
        Assert.False(Directory.Exists(repo));
        Assert.False(Directory.Exists(plain));
        Assert.Empty(scratch.Directories);
    }

    // git's loose objects are read-only; a plain recursive delete refuses them on Windows (2026-09-19).
    [Fact]
    public void DisposeDeletesReadOnlyFilesToo()
    {
        string prefix = $"claustrum-scratch-{Guid.NewGuid():N}-";
        ScratchRoot scratch = new(prefix);
        string directory = scratch.CreateDirectory();
        string file = Path.Combine(directory, "object");
        File.WriteAllText(file, "loose object\n");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        scratch.Dispose();

        Assert.Empty(Leftovers(prefix));
    }

    [Fact]
    public void DisposeOfARootThatCreatedNothingOrWasAlreadyDisposedIsHarmless()
    {
        ScratchRoot scratch = new($"claustrum-scratch-{Guid.NewGuid():N}-");

        scratch.Dispose();
        scratch.Dispose();

        Assert.Empty(scratch.Directories);
    }

    // The scratch repo must not inherit a developer's global git settings: a global commit.gpgsign or
    // core.hooksPath would otherwise decide what the fixtures' commits do.
    [Fact]
    public void ARepoIsPinnedToItsOwnHooksDirectoryAndDoesNotSign()
    {
        using ScratchRoot scratch = new($"claustrum-scratch-{Guid.NewGuid():N}-");
        string repo = scratch.CreateRepo();

        Assert.Equal(Path.Combine(repo, ".git", "hooks"), GitRepo.Run(repo, "config", "--get", "core.hooksPath"));
        Assert.Equal("false", GitRepo.Run(repo, "config", "--get", "commit.gpgsign"));
        Assert.Equal("main", GitRepo.Run(repo, "symbolic-ref", "--short", "HEAD"));
    }
}
