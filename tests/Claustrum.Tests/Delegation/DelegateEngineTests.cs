using System.Diagnostics;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
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
public sealed class DelegateEngineTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly string cwd = CreateRepo();

    // A real git repo: git's read-only loose objects defeat a plain recursive delete on Windows.
    // CLAUSTRUM_PARENT_JOB is reset here on every test regardless of whether it set one: the fixture's
    // HomeRedirectPlatform is one instance shared by every class in this collection (issue #9 tester
    // finding — AppServicesHomeFixture's own doc comment), so a tree id left behind would leak into
    // JobManagerTests/ClaustrumToolsTests or the next test of this class.
    public void Dispose()
    {
        TempTree.Delete(cwd);
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
    }

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

    // Review finding: the worktree and branch were created before Runner's own gates ran, and nothing
    // removed them when the run never reached a result.json — which is exactly the state `jobs clean`
    // refuses to touch, so the orphan was permanent.
    [Fact]
    public async Task ARunThatNeverProducesAResultLeavesNoWorktreeOrBranchBehindAsync()
    {
        DelegateRequest request = MissingBackendRequest(maxParallel: 3) with
        {
            // Rejected by Runner.ValidateTimeout, before any job output exists.
            Overrides = new ConfigOverrides(Backend: "nonexistent", TimeoutSeconds: 0),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => DelegateEngine.RunAsync(request, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees")) &&
            Directory.EnumerateDirectories(Path.Combine(cwd, ".claustrum", "worktrees")).Any());
        Assert.DoesNotContain(ListBranches(cwd), branch => branch.StartsWith("claustrum/", StringComparison.Ordinal));
    }

    // §D4's tree budget, through the max_parallel > 1 path — a spent-or-reserved-exhausted tree must
    // be refused before isolating (NOTES.md "A refused child ... is refused before it is isolated").
    [Fact]
    public async Task ExhaustedTreeRefusesBeforeIsolatingAndReleasesTheSlotAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        SaveCast(cwd, budgetUsd: 5.00m, maxParallel: 2);
        string ledgerDirectory = BudgetLedger.DirectoryFor(fixture.Platform, treeId);
        SeedFinishedLedgerEntry(ledgerDirectory, "seed-job", cap: 5.00m, cost: 5.00m);

        RunResult result = await DelegateEngine.RunAsync(CastRequest(cwd, new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Contains("nothing left", result.Error, StringComparison.Ordinal);
        AssertRefusedBeforeIsolating(result, ledgerDirectory);
    }

    // Step 4's refusal ("--budget X exceeds it") rather than step 3's ("nothing left"): a remainder
    // that is not zero but is smaller than the explicit --budget asked for.
    [Fact]
    public async Task ExplicitBudgetOverTheRemainderIsRefusedBeforeIsolatingAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        SaveCast(cwd, budgetUsd: 5.00m, maxParallel: 2);
        string ledgerDirectory = BudgetLedger.DirectoryFor(fixture.Platform, treeId);
        SeedFinishedLedgerEntry(ledgerDirectory, "seed-job", cap: 4.90m, cost: 4.90m);

        RunResult result = await DelegateEngine.RunAsync(
            CastRequest(cwd, new ConfigOverrides(Backend: "nonexistent", BudgetUsd: 0.50m)), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Contains("exceeds it", result.Error, StringComparison.Ordinal);
        AssertRefusedBeforeIsolating(result, ledgerDirectory);
    }

    // An admitted job still isolates normally, and a cost-less path (BackendMissing: nothing ran)
    // closes its reservation at $0 rather than leaving it reserved forever (NOTES.md "The paths where
    // nothing ran still charge $0").
    [Fact]
    public async Task AdmittedIsolatedJobRunsInAWorktreeAndClosesTheLedgerEntryAtCostZeroAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        SaveCast(cwd, budgetUsd: 5.00m, maxParallel: 2);

        RunResult result = await DelegateEngine.RunAsync(CastRequest(cwd, new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.NotNull(result.Worktree);
        Assert.True(Directory.Exists(result.Worktree));
        Assert.Equal($"claustrum/{result.JobId}", result.Branch);

        BudgetLedgerState state = await BudgetLedger.ReadAsync(fixture.Platform, treeId);
        BudgetLedgerRow row = Assert.Single(state.Rows, r => r.Entry.JobId == result.JobId);
        Assert.Equal(BudgetEntryState.Done, row.State);
        Assert.Equal(0m, row.Entry.Cost);
    }

    // NOTES.md "The gaps this shape still accepts, deliberately": a throw between admission and
    // Runner (here, `git worktree add` on a cwd that is no repo) must surface, not be swallowed, and
    // the reservation it leaves behind must count as abandoned ($0) for the very next admission rather
    // than freezing that slice of the tree forever.
    [Fact]
    public async Task ThrowAfterAdmissionBeforeRunnerLeavesTheFullRemainderForTheNextAdmissionAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        string nonGitCwd = Directory.CreateTempSubdirectory("claustrum-delegate-engine-nongit-").FullName;
        try
        {
            SaveCast(nonGitCwd, budgetUsd: 5.00m, maxParallel: 2);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                DelegateEngine.RunAsync(CastRequest(nonGitCwd, new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken));

            decimal remaining = await BudgetLedger.PeekRemainingAsync(fixture.Platform, new JobTreeBudget(treeId, 5.00m));
            Assert.Equal(5.00m, remaining);
        }
        finally
        {
            Directory.Delete(nonGitCwd, recursive: true);
        }
    }

    // §D1: a tree exists only when CLAUSTRUM_PARENT_JOB says so — max_parallel alone must not create
    // one (NOTES.md "Tree budget accounting is a file ledger": "no tree ⇒ ... no budget/ directory is
    // created at all").
    [Fact]
    public async Task NoParentJobMeansNoBudgetDirectoryIsEverCreatedAsync()
    {
        SaveCast(cwd, budgetUsd: 5.00m, maxParallel: 2);
        string budgetRoot = Path.Combine(fixture.HomeDirectory, ".claustrum", "budget");
        // Diffed rather than asserted absent outright: budgetRoot is shared by every test in this
        // fixture-wide home (AppServicesHomeFixture's own doc comment), so an earlier test's own tree
        // may already have created it — proving *this* run added nothing is still exact.
        string[] before = Directory.Exists(budgetRoot) ? Directory.GetDirectories(budgetRoot) : [];

        await DelegateEngine.RunAsync(CastRequest(cwd, new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken);

        string[] after = Directory.Exists(budgetRoot) ? Directory.GetDirectories(budgetRoot) : [];
        Assert.Equal(before.OrderBy(path => path, StringComparer.Ordinal), after.OrderBy(path => path, StringComparer.Ordinal));
    }

    // CastBudget's own doc comment: budget_usd: null on an active cast means unlimited and must not
    // fall back to any default — including the ledger itself, which only exists for a bounded budget.
    [Fact]
    public async Task CastBudgetUsdNullMeansNoLedgerEvenInsideATreeAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        SaveCast(cwd, budgetUsd: null, maxParallel: 2);

        RunResult result = await DelegateEngine.RunAsync(CastRequest(cwd, new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.NotNull(result.Worktree);
        Assert.False(Directory.Exists(BudgetLedger.DirectoryFor(fixture.Platform, treeId)));
    }

    private void AssertRefusedBeforeIsolating(RunResult result, string ledgerDirectory)
    {
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees", result.JobId)));
        Assert.DoesNotContain(ListBranches(cwd), branch => branch == $"claustrum/{result.JobId}");

        // The slot was taken to ask, then released (NOTES.md "Unlike the peek, this path *does*
        // create the slot file ... it takes a slot in order to ask, then releases it"): openable
        // exclusively means nobody still holds it.
        string lockPath = Path.Combine(cwd, ".claustrum", "locks", $"{RoleConcurrencyGate.KeyFor("default", "builder")}.0.lock");
        using FileStream openable = new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(File.Exists(Path.Combine(ledgerDirectory, $"{result.JobId}.json")));
    }

    private static DelegateRequest CastRequest(string atCwd, ConfigOverrides overrides)
    {
        (string tier, ConfigOverrides resolvedOverrides, CastBudget? castBudget, int? maxParallel, string? castName) =
            CastApplication.Resolve(atCwd, "builder", castName: null, tierFlag: null, overrides);

        return new DelegateRequest(
            Role: "builder",
            Brief: "hi",
            Cwd: atCwd,
            Tier: tier,
            Overrides: resolvedOverrides,
            ResumeSession: null,
            AttachFiles: [],
            Env: [],
            Stream: false,
            DiffCapBytes: 64 * 1024,
            CastBudget: castBudget,
            MaxParallel: maxParallel,
            CastName: castName);
    }

    private static void SaveCast(string atCwd, decimal? budgetUsd, int maxParallel) => CastStore.Save(atCwd, new Cast(
        "default", "1.0.0", new CastArchitect("host"),
        new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null, MaxParallel: maxParallel) },
        BudgetUsd: budgetUsd));

    // Written directly rather than through a real admission: BudgetLedgerEntry goes through
    // ClaustrumJsonContext like every other DTO (AGENTS.md), so this is the shape NOTES.md "Tree
    // budget accounting is a file ledger" documents, hand-seeded as already finished.
    private static void SeedFinishedLedgerEntry(string directory, string jobId, decimal cap, decimal cost)
    {
        Directory.CreateDirectory(directory);
        BudgetLedgerEntry entry = new(jobId, "seed", cap, cost, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory, $"{jobId}.json"), JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.BudgetLedgerEntry));
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
