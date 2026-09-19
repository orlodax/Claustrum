using System.Diagnostics;
using Claustrum.Core.Git;
// See WorktreeSnapshotTests.cs for why this is a rename, not an alias to the colliding simple name.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Core.Tests.Git;

// Exercises JobWorktree against a real temporary git repo, the same way WorktreeSnapshotTests does.
public sealed class JobWorktreeTests
{
    [Fact]
    public async Task AddCreatesAWorktreeDirectoryOnANewBranchAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");

        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);

        Assert.True(Directory.Exists(info.Path));
        Assert.True(File.Exists(Path.Combine(info.Path, "seed.txt")));
        Assert.Equal("claustrum/job-1", info.Branch);
        Assert.Equal(Path.Combine(dir, ".claustrum", "worktrees", "job-1"), info.Path);
    }

    [Fact]
    public async Task ChangesMadeInsideTheWorktreeAreInvisibleFromTheMainCheckoutAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");
        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);

        File.WriteAllText(Path.Combine(info.Path, "builder.txt"), "hi\n");

        Assert.False(File.Exists(Path.Combine(dir, "builder.txt")));
    }

    [Fact]
    public async Task RemoveDeletesTheWorktreeDirectoryButKeepsTheBranchAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");
        JobWorktreeInfo info = await JobWorktree.AddAsync(dir, "job-1", ct);
        File.WriteAllText(Path.Combine(info.Path, "builder.txt"), "hi\n");
        RunGit(info.Path, "add", "-A");
        RunGit(info.Path, "commit", "-q", "-m", "builder work");

        await JobWorktree.RemoveAsync(dir, "job-1", ct);

        Assert.False(Directory.Exists(info.Path));
        Assert.Contains("claustrum/job-1", ListBranches(dir));
    }

    [Fact]
    public async Task TwoJobsGetTwoIndependentWorktreesAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = CreateRepo();
        Commit(dir, "seed.txt", "seed\n");

        JobWorktreeInfo first = await JobWorktree.AddAsync(dir, "job-1", ct);
        JobWorktreeInfo second = await JobWorktree.AddAsync(dir, "job-2", ct);

        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(first.Branch, second.Branch);
        Assert.True(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
    }

    private static string[] ListBranches(string dir)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("branch");
        startInfo.ArgumentList.Add("--format=%(refname:short)");

        using SystemProcess process = SystemProcess.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string CreateRepo()
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-worktree-").FullName;
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
