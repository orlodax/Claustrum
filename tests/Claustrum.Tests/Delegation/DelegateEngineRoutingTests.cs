using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #74: `DelegateRequest.Isolate` sends a run down the isolated path whatever MaxParallel says (the architect `coordinate`
// spawns, which takes no slot), and a cwd in no git repository runs in place with a warning instead of dying after the
// mint (H7). In-process, against a real repository and the fake `claude` (IsolatedRepo) or the unregistered backend
// "nonexistent", which Runner answers with backend_missing before anything is spawned — a plain directory's
// claustrum.json is never read, so no fake could be configured there and none is needed.
// AppServicesHomeFixture keeps every job out of the real ~/.claustrum/jobs.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineRoutingTests(AppServicesHomeFixture fixture) : IDisposable
{

    private readonly IsolatedRepo repo = new();
    private readonly List<string> plainDirectories = [];

    public void Dispose()
    {
        repo.Dispose();
        foreach (string directory in plainDirectories)
            TempTree.Delete(directory);

        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PlainDirectory()
    {
        string directory = Directory.CreateTempSubdirectory("claustrum-routing-plain-").FullName;
        plainDirectories.Add(directory);
        return directory;
    }

    private static DelegateRequest Request(
        string cwd, int? maxParallel = null, bool isolate = false, string? branch = null, string backend = "claude", int? timeoutSeconds = null) => new(
        Role: "builder",
        Brief: "do the task",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: backend, TimeoutSeconds: timeoutSeconds),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        MaxParallel: maxParallel,
        CastName: "default",
        Branch: branch,
        Isolate: isolate);

    private static string InPlaceWarning(string cwd) => $"no git repository at {cwd}: ran in place, no worktree, no commit";

    private static string LockDirectory(string cwd) => Path.Combine(cwd, ".claustrum", "locks");

    private static string SlotFile(string cwd, int index) =>
        Path.Combine(cwd, ".claustrum", "locks", $"{RoleConcurrencyGate.KeyFor("default", "builder")}.{index}.lock");

    private JsonElement OnDisk(RunResult result)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), result.JobId, "result.json")));
        return document.RootElement.Clone();
    }

    // ---- Isolate ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task IsolateAloneRunsInAWorktreeOnItsOwnBranchAndTakesNoSlotAsync()
    {
        repo.Script(new FakeClaudeScript { Writes = [("by-the-run.txt", "x")], Summary = "wrote it" });

        RunResult result = await DelegateEngine.RunAsync(Request(repo.Repo, isolate: true), Ct);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal($"claustrum/{result.JobId}", result.Branch);
        Assert.Equal(JobWorktree(result.JobId), result.Worktree);
        Assert.NotNull(result.Commit);
        Assert.True(Directory.Exists(result.Worktree));
        Assert.False(Directory.Exists(LockDirectory(repo.Repo)), "no max_parallel means no gate, and an Isolate run must not invent one");
        Assert.Equal(result.Branch, OnDisk(result).GetProperty("branch").GetString());
    }

    [Fact]
    public async Task IsolateWithMaxParallelOneIsIsolatedAndStillGatedAsync()
    {
        repo.Script(new FakeClaudeScript { Writes = [("by-the-run.txt", "x")] });

        RunResult result = await DelegateEngine.RunAsync(Request(repo.Repo, maxParallel: 1, isolate: true), Ct);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.NotNull(result.Worktree);
        Assert.Equal($"claustrum/{result.JobId}", result.Branch);
        Assert.True(File.Exists(SlotFile(repo.Repo, 0)), "a cap of 1 goes through the slot pool even when the run is isolated");
        Assert.False(File.Exists(SlotFile(repo.Repo, 1)));
    }

    // The contrast the flag exists for: a numeric cap of 1 alone runs in the requested cwd (#58), no worktree.
    [Fact]
    public async Task MaxParallelOneWithoutIsolateStaysInPlaceAsync()
    {
        repo.Script(new FakeClaudeScript());

        RunResult result = await DelegateEngine.RunAsync(Request(repo.Repo, maxParallel: 1), Ct);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
    }

    [Fact]
    public async Task IsolateInARepositoryDoesNotAddTheNoGitWarningAsync()
    {
        repo.Script(new FakeClaudeScript());

        RunResult result = await DelegateEngine.RunAsync(Request(repo.Repo, isolate: true), Ct);

        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("no git repository", StringComparison.Ordinal));
    }

    // ---- no repository (H7) -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ANumericCapAboveOneInAPlainDirectoryRunsInPlaceWithTheWarningFirstOnDiskAndInTheReturnAsync(int cap)
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, maxParallel: cap, backend: "nonexistent"), Ct);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
        Assert.Null(result.Commit);
        Assert.Equal(InPlaceWarning(plain), result.Warnings[0]);
        JsonElement onDisk = OnDisk(result);
        Assert.Equal(InPlaceWarning(plain), onDisk.GetProperty("warnings")[0].GetString());
        Assert.False(Directory.Exists(Path.Combine(plain, ".claustrum", "worktrees")));
    }

    // N runs in one tree would read each other's edits into their receipts, so the in-place fallback queues at ONE
    // whatever the cast says: with slot 0 held, a `max_parallel: 2` run times out waiting for "all 1" slot.
    [Fact]
    public async Task TheInPlaceFallbackQueuesAtACapOfOneNotTheCastsCapAsync()
    {
        string plain = PlainDirectory();
        await using RoleConcurrencyGate held = await RoleConcurrencyGate.AcquireAsync(plain, RoleConcurrencyGate.KeyFor("default", "builder"), 1, TimeSpan.FromSeconds(5), Ct);

        RunResult result = await DelegateEngine.RunAsync(Request(plain, maxParallel: 2, backend: "nonexistent", timeoutSeconds: 1), Ct);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("all 1 '", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(SlotFile(plain, 1)), "a second slot file means the cap was taken as 2");
    }

    [Fact]
    public async Task TheInPlaceFallbackHoldsAndReleasesSlotZeroAsync()
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, maxParallel: 2, backend: "nonexistent"), Ct);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.True(File.Exists(SlotFile(plain, 0)));
        Assert.False(File.Exists(SlotFile(plain, 1)));
        using FileStream openable = new(SlotFile(plain, 0), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(openable);
    }

    [Fact]
    public async Task IsolateInAPlainDirectoryRunsInPlaceTakesNoSlotAndWarnsAsync()
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, isolate: true, backend: "nonexistent"), Ct);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Null(result.Worktree);
        Assert.Equal(InPlaceWarning(plain), Assert.Single(result.Warnings));
        Assert.False(Directory.Exists(LockDirectory(plain)));
    }

    [Fact]
    public async Task IsolateWithMaxParallelOneInAPlainDirectoryRunsInPlaceGatedAtOneAsync()
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, maxParallel: 1, isolate: true, backend: "nonexistent"), Ct);

        Assert.Equal(InPlaceWarning(plain), Assert.Single(result.Warnings));
        Assert.True(File.Exists(SlotFile(plain, 0)));
    }

    // No cap and no flag: the old in-place run, and nothing to warn about — the warning is for a run that WANTED a worktree.
    [Fact]
    public async Task APlainInPlaceRunInAPlainDirectoryCarriesNoWarningAsync()
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, backend: "nonexistent"), Ct);

        Assert.Empty(result.Warnings);
    }

    // `--branch` names a branch, which a directory with no repository cannot have: refused as before, not run in place.
    [Fact]
    public async Task ABranchWithoutARepositoryIsStillRefusedAndNeverRunsInPlaceAsync()
    {
        string plain = PlainDirectory();

        RunResult result = await DelegateEngine.RunAsync(Request(plain, branch: "feature/x", backend: "nonexistent"), Ct);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal($"--branch feature/x: no local branch of that name in {plain} (it takes an existing branch, e.g. claustrum/<job id>)", result.Error);
        Assert.Null(result.Worktree);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("no git repository", StringComparison.Ordinal));
    }

    // A repository with no commit still takes the isolated path (there IS a `.git`) and fails after the mint — recorded
    // behaviour (NOTES.md H7), pinned so a change to it is a decision.
    [Fact]
    public async Task ARepositoryWithNoCommitStillTakesTheIsolatedPathAndFailsToBranchAsync()
    {
        string commitLess = PlainDirectory();
        TestGit.Run(commitLess, "init", "-q", "-b", "main");

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DelegateEngine.RunAsync(Request(commitLess, isolate: true, backend: "nonexistent"), Ct));

        Assert.Contains("no commit to branch from", thrown.Message, StringComparison.Ordinal);
    }

    private string JobWorktree(string jobId) => repo.WorktreePath(jobId);
}
