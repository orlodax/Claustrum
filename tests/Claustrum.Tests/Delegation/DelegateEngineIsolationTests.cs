using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// DelegateEngine's isolated path with a real repository and a fake `claude` script (IsolatedRepo): the
// deny list #62 adds, the abandoned-run cleanup F9 made careful about the work a run left, and a refusal
// after admission closing its ledger entry at $0 (F10). In-process, so the engine's own exceptions and the
// job directory it was handed are visible; the cross-process behaviour is RunCapEndToEndTests' and
// IsolatedRunEndToEndTests'. AppServicesHomeFixture keeps every job out of the real ~/.claustrum/jobs.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineIsolationTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose()
    {
        repo.Dispose();
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DelegateRequest Request(int? maxParallel, string? branch = null, string backend = "claude", int? timeoutSeconds = null) => new(
        Role: "builder",
        Brief: "do the task",
        Cwd: repo.Repo,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: backend, TimeoutSeconds: timeoutSeconds),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        MaxParallel: maxParallel,
        CastName: "default",
        Branch: branch);

    private static async Task<RunResult> RunAsync(DelegateRequest request) => await DelegateEngine.RunAsync(request, Ct);

    // A job minted by the caller (delegate_async's shape) whose result.json cannot be written: a directory
    // sits where the file goes. Once the process has run, Runner's finish funnel throws out of the engine.
    private JobPaths JobWithAnUnwritableResult()
    {
        JobPaths job = JobDirectory.Create(fixture.Platform);
        Directory.CreateDirectory(job.ResultJson);
        return job;
    }

    private static ResolvedRole Role(params string[] deny) =>
        new("builder", "system", "claude", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, deny), Blind: false, HasReport: true);

    [Fact]
    public void IsolatedRoleAddsCheckoutAndSwitchToTheDenyListAfterTheRolesOwn()
    {
        ResolvedRole isolated = DelegateEngine.IsolatedRole(Role("git push"));

        Assert.Equal(["git push", "git checkout", "git switch"], isolated.Permission.Deny);
        Assert.Equal(PermissionLevel.EditShell, isolated.Permission.Level);
        Assert.Equal("builder", isolated.Name);
        Assert.Equal("system", isolated.SystemPrompt);
        Assert.Equal("claude", isolated.Backend);
        Assert.True(isolated.HasReport);
    }

    [Fact]
    public void IsolatedRoleNeverDuplicatesAnEntryTheRoleAlreadyDeniesAndIsIdempotent()
    {
        ResolvedRole once = DelegateEngine.IsolatedRole(Role("git checkout", "git push"));
        ResolvedRole twice = DelegateEngine.IsolatedRole(once);

        Assert.Equal(["git checkout", "git push", "git switch"], once.Permission.Deny);
        Assert.Equal(once.Permission.Deny, twice.Permission.Deny);
        Assert.Single(twice.Permission.Deny, entry => entry == "git checkout");
    }

    [Fact]
    public void IsolatedRoleLeavesTheRoleItWasGivenAloneSoAnInPlaceRunIsUnchanged()
    {
        ResolvedRole original = Role("git push");

        _ = DelegateEngine.IsolatedRole(original);

        Assert.Equal(["git push"], original.Permission.Deny);
        Assert.Equal(["git checkout", "git switch"], JobWorktree.IsolationDeny);
    }

    [Fact]
    public void IsolatedRoleOnARoleWithNoDenyStillDeniesBoth()
    {
        Assert.Equal(["git checkout", "git switch"], DelegateEngine.IsolatedRole(Role()).Permission.Deny);
    }

    // F9: the run's result.json could not be written after the runner had committed. Force-removing the
    // worktree and `branch -D`-ing the branch, as the cleanup used to, would have destroyed that commit.
    [Fact]
    public async Task AnUnwritableResultAfterTheRunnerCommittedKeepsTheBranchWithTheWorkAndRemovesTheCleanWorktreeAsync()
    {
        repo.WriteCast(maxParallel: 3);
        repo.Script(new FakeClaudeScript { Writes = [("work.txt", "the work")] });
        JobPaths job = JobWithAnUnwritableResult();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(maxParallel: 3));

        await Assert.ThrowsAnyAsync<SystemException>(() => DelegateEngine.RunAsync(prepared, job, Ct));

        string branch = $"claustrum/{job.Id}";
        Assert.Contains(branch, TestGit.Branches(repo.Repo));
        Assert.Equal(1, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.Equal("work.txt", TestGit.Run(repo.Repo, "show", "--name-only", "--format=", branch));
        Assert.False(Directory.Exists(repo.WorktreePath(job.Id)));
        Assert.Single(TestGit.WorktreePaths(repo.Repo));
    }

    // An unmoved branch with a dirty worktree is a refused commit followed by an unwritable result.json:
    // the run's work is in that worktree only, so it is left, and the failure says so.
    [Fact]
    public async Task AnUnwritableResultAfterARefusedCommitLeavesTheWorktreeWithItsWorkAndSaysSoAsync()
    {
        repo.WriteCast(maxParallel: 3);
        repo.Script(new FakeClaudeScript { Writes = [("work.txt", "the work")] });
        TestGit.WriteHook(repo.Repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");
        JobPaths job = JobWithAnUnwritableResult();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(maxParallel: 3));

        AggregateException ex = await Assert.ThrowsAsync<AggregateException>(() => DelegateEngine.RunAsync(prepared, job, Ct));

        Assert.Contains($"(and cleaning up {repo.WorktreePath(job.Id)} failed)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("work.txt", TestGit.Status(repo.WorktreePath(job.Id)), StringComparison.Ordinal);
        // The Windows fake writes through cmd's `echo`, which ends the line with CRLF.
        Assert.Equal("the work\n", File.ReadAllText(Path.Combine(repo.WorktreePath(job.Id), "work.txt")).ReplaceLineEndings("\n"));
        Assert.Contains($"claustrum/{job.Id}", TestGit.Branches(repo.Repo));
    }

    // #63: the branch of a `--branch` run was given to it, so even an abandoned, untouched run leaves it at
    // the commit it had, while its own worktree goes.
    [Fact]
    public async Task AnAbandonedBranchRunRemovesItsWorktreeButNeverTheBranchItWasGivenAsync()
    {
        repo.WriteCast(maxParallel: 3);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")] });
        RunResult first = await RunAsync(Request(maxParallel: 3));
        string branch = first.Branch ?? "";
        string tip = TestGit.RevParse(repo.Repo, $"refs/heads/{branch}");
        JobPaths job = JobWithAnUnwritableResult();
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(maxParallel: null, branch: branch, backend: "nonexistent"));

        await Assert.ThrowsAnyAsync<SystemException>(() => DelegateEngine.RunAsync(prepared, job, Ct));

        Assert.Equal(tip, TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"));
        Assert.False(Directory.Exists(repo.WorktreePath(job.Id)));
        Assert.DoesNotContain($"claustrum/{job.Id}", TestGit.Branches(repo.Repo));
        Assert.Single(TestGit.WorktreePaths(repo.Repo));
    }

    // F10 + F11: a refusal after admission is a receipt, and it closes the reservation at $0 through
    // Runner's funnel — otherwise the next admission would read the entry as abandoned. The branch `main` is
    // checked out in the repository itself, which `--branch` refuses.
    [Fact]
    public async Task ABranchRefusalAfterAdmissionIsAReceiptAndClosesItsLedgerEntryAtZeroAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        CastStore.Save(repo.Repo, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: null, Backend: "claude", Tier: null, MaxParallel: 2) },
            BudgetUsd: 5.00m));
        (string tier, ConfigOverrides overrides, CastBudget? castBudget, int? maxParallel, string? castName) =
            CastApplication.Resolve(repo.Repo, "builder", castName: null, tierFlag: null, new ConfigOverrides());
        DelegateRequest request = Request(maxParallel: maxParallel, branch: "main") with { Tier = tier, Overrides = overrides, CastBudget = castBudget, CastName = castName };

        RunResult result = await RunAsync(request);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("--branch main: already checked out in", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
        BudgetLedgerState state = await BudgetLedger.ReadAsync(fixture.Platform, treeId);
        BudgetLedgerRow row = Assert.Single(state.Rows, r => r.Entry.JobId == result.JobId);
        Assert.Equal(BudgetEntryState.Done, row.State);
        Assert.Equal(0m, row.Entry.Cost);
        Assert.Equal(0m, state.Reserved);
        Assert.Equal(5.00m, await BudgetLedger.PeekRemainingAsync(fixture.Platform, new JobTreeBudget(treeId, 5.00m)));
    }

    // #58: the in-place gated path mints a job only for the gate-timeout refusal; the run's receipt is the
    // job's result.json, and nothing was isolated.
    [Fact]
    public async Task AGateTimeoutAtOneOnTheInPlacePathIsAReceiptInAJobItMintedAsync()
    {
        string jobsRoot = JobDirectory.ResolveRoot(fixture.Platform);
        string[] before = Directory.Exists(jobsRoot) ? Directory.GetDirectories(jobsRoot) : [];

        await using (await RoleConcurrencyGate.AcquireAsync(repo.Repo, RoleConcurrencyGate.KeyFor("default", "builder"), 1, TimeSpan.FromSeconds(5), Ct))
        {
            RunResult result = await RunAsync(Request(maxParallel: 1, timeoutSeconds: 1));

            Assert.Equal(RunStatus.Failed, result.Status);
            Assert.Contains("all 1 'default__builder' slots", result.Error, StringComparison.Ordinal);
            Assert.Null(result.Worktree);
            string added = Assert.Single(Directory.GetDirectories(jobsRoot).Except(before));
            Assert.True(JobDirectory.HasResult(jobsRoot, Path.GetFileName(added)));
        }

        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
    }
}
