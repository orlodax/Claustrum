namespace Claustrum.Core.Tests.Testing;

/// <summary>
/// Owns every temp directory one test class creates and deletes them all when the class is disposed, so a
/// suite run leaves nothing of its own behind in the machine's temp directory (#47: JobWorktreeTests and
/// WorktreeSnapshotTests left 84 `claustrum-git-*`, 48 `claustrum-worktree-*` and 12 `claustrum-nogit-*`
/// directories in /tmp). A git worktree lives under its repository, so deleting the repository's
/// directory takes its worktrees and their admin entries with it.
/// </summary>
public sealed class ScratchRoot(string prefix) : IDisposable
{
    private readonly List<string> directories = [];

    /// <summary>Directories created and not yet deleted, for the test that proves Dispose cleans up.</summary>
    public IReadOnlyList<string> Directories => directories;

    public string CreateDirectory()
    {
        string directory = Directory.CreateTempSubdirectory(prefix).FullName;
        directories.Add(directory);
        return directory;
    }

    /// <summary>A repository with an identity, no signing and the hooks directory pinned to its own .git.</summary>
    public string CreateRepo()
    {
        string directory = CreateDirectory();
        GitRepo.Init(directory);
        return directory;
    }

    /// <summary>
    /// <see cref="CreateRepo"/> plus a first commit of `seed.txt` and a `.gitignore` for `.claustrum/` (what
    /// `claustrum init` writes), so the worktrees and locks a run puts under the repository never show as
    /// untracked in its own status.
    /// </summary>
    public string CreateSeededRepo()
    {
        string directory = CreateRepo();
        File.WriteAllText(Path.Combine(directory, ".gitignore"), ".claustrum/\n");
        GitRepo.Commit(directory, "seed.txt", "seed\n");
        return directory;
    }

    public void Dispose()
    {
        foreach (string directory in directories)
            TempTree.Delete(directory);

        directories.Clear();
    }
}
