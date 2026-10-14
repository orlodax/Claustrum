using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #104: the job machinery (worktree, slot files, branch lock) is never created through a `.claustrum` that leaves the repo.
// The run is refused with a failed receipt naming the path (MachineryRefusedException carries it; the CLI turns it into exit 2,
// the MCP doors return it as the document), the outside directory stays empty, and a link that stays inside is fine. In process,
// against a real repository and the fake claude (IsolatedRepo): the cast cannot carry max_parallel through a linked `.claustrum`
// (the cast itself is fenced, #103), so the request is built directly, which is the shape the engine sees from a cast.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineFenceTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-fence-outside-").FullName;

    public void Dispose()
    {
        repo.Dispose();
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
        TempTree.Delete(outside);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ClaustrumPath => Path.Combine(repo.Repo, ".claustrum");

    private DelegateRequest Request(int? maxParallel, string? branch = null, int? timeoutSeconds = null) => new(
        Role: "builder",
        Brief: "do the task",
        Cwd: repo.Repo,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "claude", TimeoutSeconds: timeoutSeconds),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        MaxParallel: maxParallel,
        CastName: "default",
        Branch: branch);

    private void LinkClaustrumOut() => TestLinks.CreateDirectoryLink(ClaustrumPath, outside);

    private static void AssertRefused(RunResult receipt, string expectedPathPart)
    {
        Assert.Equal(RunStatus.Failed, receipt.Status);
        Assert.Contains("resolves outside the working directory", receipt.Error, StringComparison.Ordinal);
        Assert.Contains(expectedPathPart, receipt.Error, StringComparison.Ordinal);
    }

    private void AssertNothingCreatedOutsideAndReceiptOnDisk(RunResult receipt)
    {
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        string resultJson = Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), receipt.JobId, "result.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultJson));
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("resolves outside the working directory", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIsolatedRunThroughAClaustrumLinkOutIsRefusedAndNothingIsCreatedThereAsync()
    {
        LinkClaustrumOut();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: 2), Ct));

        AssertRefused(ex.Receipt, Path.Combine(".claustrum", "worktrees"));
        AssertNothingCreatedOutsideAndReceiptOnDisk(ex.Receipt);
    }

    // max_parallel 1 does not isolate, but it takes the slot lock under `.claustrum/locks`.
    [Fact]
    public async Task AnInPlaceRunAtMaxParallelOneThroughAClaustrumLinkOutIsRefusedAtTheSlotLockAsync()
    {
        LinkClaustrumOut();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: 1), Ct));

        AssertRefused(ex.Receipt, Path.Combine(".claustrum", "locks"));
        AssertNothingCreatedOutsideAndReceiptOnDisk(ex.Receipt);
    }

    [Fact]
    public async Task ABranchRunThroughAClaustrumLinkOutIsRefusedAndNothingIsCreatedThereAsync()
    {
        repo.Git("branch", "feature/x");
        LinkClaustrumOut();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => DelegateEngine.RunAsync(Request(maxParallel: null, branch: "feature/x"), Ct));

        AssertRefused(ex.Receipt, ".claustrum");
        AssertNothingCreatedOutsideAndReceiptOnDisk(ex.Receipt);
        Assert.DoesNotContain("job", TestGit.WorktreePaths(repo.Repo).Skip(1).Select(Path.GetFileName));
    }

    [Fact]
    public async Task DelegateOverMcpReturnsTheFailedDocumentWithThePathInErrorAsync()
    {
        repo.Git("branch", "feature/x");
        LinkClaustrumOut();

        string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "do the task", cwd: repo.Repo, backend: "claude", branch: "feature/x", cancellationToken: Ct);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        string error = document.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("resolves outside the working directory", error, StringComparison.Ordinal);
        Assert.Contains(".claustrum", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task DelegateAsyncOverMcpEndsInTheFailedDocumentWithThePathInErrorAsync()
    {
        repo.Git("branch", "feature/x");
        LinkClaustrumOut();

        using JsonDocument started = JsonDocument.Parse(ClaustrumTools.DelegateStart(role: "builder", brief: "do the task", cwd: repo.Repo, backend: "claude", branch: "feature/x"));
        string jobId = started.RootElement.GetProperty("job_id").GetString() ?? "";
        using JsonDocument result = JsonDocument.Parse(await ClaustrumTools.JobResultAsync(jobId));

        Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
        Assert.Contains("resolves outside the working directory", result.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public async Task AClaustrumLinkThatStaysInsideTheRepoIsAllowedAsync()
    {
        string store = Directory.CreateDirectory(Path.Combine(repo.Repo, "dotfiles", "claustrum")).FullName;
        TestLinks.CreateDirectoryLink(ClaustrumPath, store);
        repo.Script(new FakeClaudeScript { Writes = [("work.txt", "the work")], Summary = "wrote work.txt" });

        RunResult result = await DelegateEngine.RunAsync(Request(maxParallel: 2), Ct);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.NotNull(result.Worktree);
        Assert.Contains($"claustrum/{result.JobId}", TestGit.Branches(repo.Repo));
    }

    // The waits can take minutes: a `.claustrum/worktrees` link that appears while the run queues is caught before admission.
    [Fact]
    public async Task AWorktreesLinkAppearingDuringTheSlotWaitIsRefusedAndNoReservationIsLeftAsync()
    {
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;
        CastStore.Save(repo.Repo, new Cast(
            "default", "1.0.0", new CastArchitect("host"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: null, Backend: "claude", Tier: null, MaxParallel: 2) },
            BudgetUsd: 5.00m));
        (string tier, ConfigOverrides overrides, CastBudget? castBudget, int? maxParallel, string? castName) =
            CastApplication.Resolve(repo.Repo, "builder", castName: null, tierFlag: null, new ConfigOverrides(Backend: "claude", TimeoutSeconds: 60));
        DelegateRequest request = Request(maxParallel: maxParallel) with { Tier = tier, Overrides = overrides, CastBudget = castBudget, CastName = castName };
        string key = RoleConcurrencyGate.KeyFor("default", "builder");

        await using RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(repo.Repo, key, 2, TimeSpan.FromSeconds(5), Ct);
        RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(repo.Repo, key, 2, TimeSpan.FromSeconds(5), Ct);
        Task<RunResult> run = Task.Run(() => DelegateEngine.RunAsync(request, Ct), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.False(run.IsCompleted, "the run should be waiting for a slot");
        TestLinks.CreateDirectoryLink(Path.Combine(ClaustrumPath, "worktrees"), outside);
        await second.DisposeAsync();

        MachineryRefusedException ex = await Assert.ThrowsAsync<MachineryRefusedException>(() => run);

        AssertRefused(ex.Receipt, Path.Combine(".claustrum", "worktrees"));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        BudgetLedgerState state = await BudgetLedger.ReadAsync(fixture.Platform, treeId);
        Assert.Empty(state.Rows);
        Assert.Equal(0m, state.Reserved);
    }
}
