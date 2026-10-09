using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Git;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// #74 through Runner: the commit path JobWorktreeLeftOutTests and JobWorktreeConflictTests drive directly, reached the way
// a run reaches it. What matters here is what only Runner adds: git's `add` stderr lands in the JOB's stderr.log, a worktree
// left mid-operation is read by the runner's own verify BEFORE the after-snapshot (so the receipt keeps the changes), and
// the caller's pre-run warnings (RunOptions.PreRunWarnings — DelegateEngine's no-git fallback) are on result.json too,
// first, whatever the run did afterwards. A ScriptedBackend runs a real shell command as the "role", in a real worktree.
public sealed class RunnerCommitPathTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");
    private readonly string homeDir;
    private readonly string repo;

    public RunnerCommitPathTests()
    {
        homeDir = scratch.CreateDirectory();
        repo = scratch.CreateSeededRepo();
    }

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ResolvedRole MakeRole() =>
        new("builder", "system prompt", "scripted", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), Blind: false, HasReport: false);

    private Runner NewRunner(IBackend backend)
    {
        HomeRedirectPlatform platform = new(homeDir);
        return new Runner(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
    }

    private static RunRequest MakeRequest(string cwd) => new(
        Role: "builder", Brief: "do it", BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
        Permission: null, BudgetUsd: null, Timeout: null, ResumeSession: null, AttachFiles: [], Env: [], Stream: false);

    private async Task<RunResult> RunInPlaceAsync(IBackend backend, string[] preRunWarnings) =>
        await NewRunner(backend).RunAsync(MakeRequest(repo), MakeRole(), new RunOptions(DiffByteCapBytes: 200_000, PreRunWarnings: preRunWarnings), Ct);

    private async Task<(RunResult Result, JobWorktreeInfo Worktree)> RunIsolatedAsync(IBackend backend, string[]? preRunWarnings = null)
    {
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        RunResult result = await NewRunner(backend).RunAsync(
            MakeRequest(worktree.Path), MakeRole(), new RunOptions(DiffByteCapBytes: 200_000, Worktree: worktree, PreRunWarnings: preRunWarnings), Ct);
        return (result, worktree);
    }

    private static string JobDirectoryOf(RunResult result) => Path.GetDirectoryName(result.LogPath) ?? "";

    private static JsonElement ReadResultJson(RunResult result)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDirectoryOf(result), "result.json")));
        return document.RootElement.Clone();
    }

    private static string[] WarningsOnDisk(RunResult result) =>
        [.. ReadResultJson(result).GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString() ?? "")];

    private static void RequireShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the role here is a POSIX shell line that drives git.");
    }

    // ---- RunOptions.PreRunWarnings ---------------------------------------------------------------------------------

    [Fact]
    public async Task PreRunWarningsAreOnTheReturnedResultAndOnTheResultJsonOnDiskAsync()
    {
        RunResult result = await RunInPlaceAsync(ScriptedBackend.Success(), ["no git repository at /x: ran in place, no worktree, no commit"]);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(["no git repository at /x: ran in place, no worktree, no commit"], result.Warnings);
        Assert.Equal(["no git repository at /x: ran in place, no worktree, no commit"], WarningsOnDisk(result));
    }

    [Fact]
    public async Task PreRunWarningsAlsoRideOnAResultTheBackendNeverProducedAsync()
    {
        RunResult result = await RunInPlaceAsync(ScriptedBackend.NotOnPath(), ["decided before the run"]);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Equal(["decided before the run"], result.Warnings);
        Assert.Equal(["decided before the run"], WarningsOnDisk(result));
    }

    // First: they were decided before the run, so they read before anything the run itself produced.
    [Fact]
    public async Task PreRunWarningsComeBeforeWhatTheRunItselfWarnsAboutAsync()
    {
        GitRepo.WriteHook(repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");
        ScriptedBackend backend = ScriptedBackend.Shell("printf 'x\\n' > made.txt", "echo x> made.txt");

        (RunResult result, _) = await RunIsolatedAsync(backend, ["decided before the run"]);

        Assert.Equal(["decided before the run", "work left uncommitted on claustrum/job-1: hook says no"], result.Warnings);
        Assert.Equal(result.Warnings, WarningsOnDisk(result));
    }

    [Fact]
    public async Task WithNoPreRunWarningsTheResultCarriesNoneOfTheirsAsync()
    {
        RunResult result = await NewRunner(ScriptedBackend.Success()).RunAsync(
            MakeRequest(repo), MakeRole(), new RunOptions(DiffByteCapBytes: 200_000), Ct);

        Assert.Empty(result.Warnings);
        Assert.Empty(WarningsOnDisk(result));
    }

    // ---- git's `add` words reach the JOB's stderr.log ---------------------------------------------------------------

    [Fact]
    public async Task APathLeftOutOfTheCommitPutsGitsAddStderrInTheJobsStderrLogAndPointsAtItAsync()
    {
        RequireShell();
        Directory.CreateDirectory(Path.Combine(repo, "a"));
        Directory.CreateDirectory(Path.Combine(repo, "b"));
        File.WriteAllText(Path.Combine(repo, "a", "x.txt"), "a\n");
        File.WriteAllText(Path.Combine(repo, "b", "x.txt"), "b\n");
        GitRepo.Run(repo, "add", "-A");
        GitRepo.Run(repo, "commit", "-q", "-m", "a and b");
        GitRepo.Run(repo, "sparse-checkout", "set", "--cone", "a");
        ScriptedBackend backend = ScriptedBackend.Shell("echo 'from the role' >&2; printf 'x\\n' > a/new.txt; mkdir -p b; printf 'x\\n' > b/new.txt", "unused");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.NotNull(result.Commit);
        Assert.Equal("a/new.txt", GitRepo.Run(repo, "show", "--name-only", "--format=", result.Commit));
        Assert.Equal(
            ["b/new.txt left out of the commit on claustrum/job-1: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)"],
            result.Warnings);
        Assert.Equal(result.Warnings, WarningsOnDisk(result));

        // The log is the backend's own stderr first, then the claustrum block appended after the process ended.
        string log = File.ReadAllText(Path.Combine(JobDirectoryOf(result), "stderr.log"));
        Assert.Contains("from the role", log, StringComparison.Ordinal);
        int block = log.IndexOf($"claustrum: git add -A in {worktree.Path}, for the commit on claustrum/job-1:", StringComparison.Ordinal);
        Assert.True(block > log.IndexOf("from the role", StringComparison.Ordinal), log);
        Assert.Contains("b/new.txt", log[block..], StringComparison.Ordinal);
    }

    // ---- mid-operation: the runner's own verify sees it before the after-snapshot --------------------------------

    [Fact]
    public async Task ARunThatLeftAConflictedMergeKeepsItsChangesCommitsNothingAndWarnsAsync()
    {
        RequireShell();
        GitRepo.Commit(repo, "f", "base\n");
        GitRepo.Run(repo, "checkout", "-q", "-b", "other");
        GitRepo.Commit(repo, "f", "other\n");
        GitRepo.Run(repo, "checkout", "-q", "main");
        ScriptedBackend backend = ScriptedBackend.Shell(
            "printf 'job\\n' > f; git add f; git commit -qm job; git merge -q other; printf 'x\\n' > real.txt", "unused");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Equal([$"work left uncommitted: {worktree.Path} has a merge in progress, not a clean claustrum/job-1"], result.Warnings);
        Assert.Contains(result.ChangedFiles, file => file.Path == "real.txt");
        Assert.Contains("UU f", GitRepo.Status(worktree.Path), StringComparison.Ordinal);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1~1"));
        Assert.Null(ReadResultJson(result).GetProperty("commit").GetString());
    }

    [Fact]
    public async Task ARunThatLeftUnresolvedConflictsWithNoHeadCommitsNothingAndWarnsAsync()
    {
        RequireShell();
        GitRepo.Commit(repo, "f", "base\n");
        GitRepo.Run(repo, "checkout", "-q", "-b", "other");
        GitRepo.Commit(repo, "f", "other\n");
        GitRepo.Run(repo, "checkout", "-q", "main");
        ScriptedBackend backend = ScriptedBackend.Shell(
            "printf 'job\\n' > f; git add f; git commit -qm job; git merge -q --squash other; printf 'x\\n' > real.txt", "unused");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Equal(
            [$"work left uncommitted: {worktree.Path} has unresolved conflicts on claustrum/job-1 — resolve or abort the operation inside the worktree, then commit there yourself"],
            result.Warnings);
        Assert.Contains(result.ChangedFiles, file => file.Path == "real.txt");
        Assert.Contains("UU f", GitRepo.Status(worktree.Path), StringComparison.Ordinal);
    }
}
