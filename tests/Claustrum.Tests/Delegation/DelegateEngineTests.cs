using System.Diagnostics;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;
// See WorktreeSnapshotTests.cs (Claustrum.Core.Tests) for why this is a rename, not an alias to the
// colliding simple name: Claustrum.Core.Process shadows System.Diagnostics.Process here too.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Tests.Delegation;

// Exercises DelegateEngine's max_parallel wiring (docs/PLAN.md §D4) against a real temp git repo and
// backend "nonexistent", which Runner rejects with Status.BackendMissing before ever touching the
// (worktree) cwd's contents — so this needs no real backend install, same trick JobManagerTests uses.
// AppServicesHomeFixture keeps the job this creates out of the real ~/.claustrum/jobs.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineTests : IDisposable
{
    private readonly string cwd = CreateRepo();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private DelegateRequest MissingBackendRequest(int? maxParallel) => new(
        Role: "builder",
        Brief: "hi",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "nonexistent"),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        MaxParallel: maxParallel);

    [Fact]
    public async Task NoMaxParallelRunsDirectlyInTheRequestedCwdAsync()
    {
        RunResult result = await DelegateEngine.RunAsync(MissingBackendRequest(maxParallel: null), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
    }

    [Fact]
    public async Task MaxParallelOneRunsDirectlyInTheRequestedCwdAsync()
    {
        RunResult result = await DelegateEngine.RunAsync(MissingBackendRequest(maxParallel: 1), TestContext.Current.CancellationToken);

        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
    }

    [Fact]
    public async Task MaxParallelAboveOneRunsInsideAnIsolatedWorktreeAsync()
    {
        RunResult result = await DelegateEngine.RunAsync(MissingBackendRequest(maxParallel: 3), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Worktree);
        Assert.True(Directory.Exists(result.Worktree));
        Assert.StartsWith(Path.Combine(cwd, ".claustrum", "worktrees"), result.Worktree, StringComparison.Ordinal);
        Assert.Equal($"claustrum/{result.JobId}", result.Branch);
    }

    [Fact]
    public async Task TwoMaxParallelRunsGetTwoDistinctWorktreesAsync()
    {
        RunResult first = await DelegateEngine.RunAsync(MissingBackendRequest(maxParallel: 3), TestContext.Current.CancellationToken);
        RunResult second = await DelegateEngine.RunAsync(MissingBackendRequest(maxParallel: 3), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Worktree, second.Worktree);
        Assert.NotEqual(first.Branch, second.Branch);
    }

    private static string CreateRepo()
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-delegate-engine-").FullName;
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "claustrum-tests");
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed\n");
        RunGit(dir, "add", "-A");
        RunGit(dir, "commit", "-q", "-m", "seed");
        return dir;
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
