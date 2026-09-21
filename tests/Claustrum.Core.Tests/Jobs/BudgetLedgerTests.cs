using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Jobs;

// BudgetLedger.AdmitAsync/CompleteAsync against a real temp ledger directory: the ledger itself
// does its file I/O directly (never through IPlatform), so only CLAUSTRUM_HOME needs redirecting to
// keep every test under a per-instance temp home. NOTES.md "Tree budget accounting is a file
// ledger" carries the admission rule this file exercises.
public sealed class BudgetLedgerTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-budget-").FullName;
    private readonly FakePlatform platform = new();

    public BudgetLedgerTests() => platform.EnvironmentVariables["CLAUSTRUM_HOME"] = home;

    public void Dispose() => Directory.Delete(home, recursive: true);

    [Fact]
    public async Task AdmitWritesAnEntryWithTheDocumentedShapeAsync()
    {
        JobTreeBudget tree = new("tree-shape", 2.00m);

        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: null);

        Assert.True(admission.Admitted);
        Assert.Equal(2.00m, admission.EffectiveCap);
        string directory = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        string livePath = Path.Combine(directory, "job-1.live");
        Assert.True(File.Exists(Path.Combine(directory, ".lock")));
        Assert.True(File.Exists(livePath));

        JsonElement root = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "job-1.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("job-1", root.GetProperty("job_id").GetString());
        Assert.Equal("builder", root.GetProperty("role").GetString());
        Assert.Equal(2.00m, root.GetProperty("cap").GetDecimal());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cost").ValueKind);
        Assert.True(root.TryGetProperty("started_at", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("finished_at").ValueKind);
        Assert.False(root.GetProperty("abandoned").GetBoolean());

        // CompleteAsync's other half of the entry's life cycle: charging deletes the .live handle.
        await admission.Reservation!.CompleteAsync(2.00m, ran: true);

        Assert.False(File.Exists(livePath));
    }

    [Fact]
    public async Task ConcurrentAdmitsOnTwoThreadsSerializeThroughTheLockAsync()
    {
        JobTreeBudget tree = new("tree-concurrent", 2.00m, Share: 2);

        Task<BudgetAdmission> firstTask = Task.Run(() => BudgetLedger.AdmitAsync(platform, tree, "job-a", "builder", requestedCap: null));
        Task<BudgetAdmission> secondTask = Task.Run(() => BudgetLedger.AdmitAsync(platform, tree, "job-b", "builder", requestedCap: null));
        BudgetAdmission[] admissions = await Task.WhenAll(firstTask, secondTask);

        // Whichever admission wins the lock race gets the whole $1.00 half; the other sees that
        // reservation and is left $0.50 — the set is deterministic even though "which job" is not.
        decimal[] caps = [.. admissions.Select(admission => admission.EffectiveCap!.Value).OrderDescending()];
        Assert.Equal([1.00m, 0.50m], caps);
        string directory = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        Assert.True(File.Exists(Path.Combine(directory, "job-a.json")));
        Assert.True(File.Exists(Path.Combine(directory, "job-b.json")));

        foreach (BudgetAdmission admission in admissions)
            await admission.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task SequentialAdmitsAfterCompletingAtZeroGetEqualSlicesAsync()
    {
        JobTreeBudget tree = new("tree-sequential", 2.00m, Share: 2);
        BudgetAdmission first = await BudgetLedger.AdmitAsync(platform, tree, "job-a", "builder", requestedCap: null);
        Assert.Equal(1.00m, first.EffectiveCap);
        await first.Reservation!.CompleteAsync(cost: 0m, ran: true);

        BudgetAdmission second = await BudgetLedger.AdmitAsync(platform, tree, "job-b", "builder", requestedCap: null);

        Assert.Equal(1.00m, second.EffectiveCap);
        await second.Reservation!.DisposeAsync();
    }

    // NOTES.md's own measured example: a sibling's granted cap counts against the tree exactly like
    // a finished cost while its .live handle is still held.
    [Fact]
    public async Task HeldLiveCapReservesAgainstASequentialSiblingAsync()
    {
        JobTreeBudget tree = new("tree-held-share1", 5.00m, Share: 1);
        BudgetAdmission holder = await BudgetLedger.AdmitAsync(platform, tree, "job-holder", "builder", requestedCap: 2.50m);

        BudgetAdmission next = await BudgetLedger.AdmitAsync(platform, tree, "job-next", "builder", requestedCap: null);

        Assert.True(next.Admitted);
        Assert.Equal(2.50m, next.Reserved);
        Assert.Equal(2.50m, next.EffectiveCap);

        await holder.Reservation!.DisposeAsync();
        await next.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task HeldLiveCapReservesAgainstAFanOutSiblingAsync()
    {
        JobTreeBudget tree = new("tree-held-share2", 5.00m, Share: 2);
        BudgetAdmission holder = await BudgetLedger.AdmitAsync(platform, tree, "job-holder", "builder", requestedCap: 2.50m);

        BudgetAdmission next = await BudgetLedger.AdmitAsync(platform, tree, "job-next", "builder", requestedCap: null);

        Assert.Equal(1.25m, next.EffectiveCap);

        await holder.Reservation!.DisposeAsync();
        await next.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task CompleteWithNullCostAndRanTrueChargesTheGrantedCapAsync()
    {
        JobTreeBudget tree = new("tree-complete-a", 1.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.40m);

        await admission.Reservation!.CompleteAsync(cost: null, ran: true);

        Assert.Equal(0.40m, (await ReadEntryAsync(tree.TreeId, "job-1")).Cost);
    }

    [Fact]
    public async Task CompleteWithNullCostAndRanFalseChargesZeroAsync()
    {
        JobTreeBudget tree = new("tree-complete-b", 1.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.40m);

        await admission.Reservation!.CompleteAsync(cost: null, ran: false);

        Assert.Equal(0m, (await ReadEntryAsync(tree.TreeId, "job-1")).Cost);
    }

    // "Charged at most once": a second CompleteAsync call on the same reservation must not overwrite
    // what the run really cost, and must not touch finished_at either.
    [Fact]
    public async Task SecondCompleteCallLeavesTheFirstChargeAndFinishedAtUntouchedAsync()
    {
        JobTreeBudget tree = new("tree-complete-c", 1.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.40m);

        await admission.Reservation!.CompleteAsync(0.02m, ran: true);
        BudgetLedgerEntry firstRead = await ReadEntryAsync(tree.TreeId, "job-1");

        await admission.Reservation.CompleteAsync(null, ran: true);
        BudgetLedgerEntry secondRead = await ReadEntryAsync(tree.TreeId, "job-1");

        Assert.Equal(0.02m, secondRead.Cost);
        Assert.Equal(firstRead.FinishedAt, secondRead.FinishedAt);
    }

    [Fact]
    public async Task DeadHolderIsMarkedAbandonedOnTheNextAdmissionAsync()
    {
        JobTreeBudget tree = new("tree-dead", 1.00m);
        BudgetAdmission first = await BudgetLedger.AdmitAsync(platform, tree, "job-dead", "builder", requestedCap: 0.40m);
        await first.Reservation!.DisposeAsync(); // releases the handle without completing: the holder "died"

        BudgetAdmission second = await BudgetLedger.AdmitAsync(platform, tree, "job-live", "builder", requestedCap: null);

        Assert.True(second.Admitted);
        Assert.Equal(1.00m, second.EffectiveCap);
        BudgetLedgerState state = await BudgetLedger.ReadAsync(platform, tree.TreeId);
        BudgetLedgerRow deadRow = state.Rows.Single(row => row.Entry.JobId == "job-dead");
        Assert.Equal(BudgetEntryState.Abandoned, deadRow.State);
        Assert.Null(deadRow.Entry.Cost);

        await second.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task UnfinishedEntryWithNoLiveFileIsAbandonedAsync()
    {
        JobTreeBudget tree = new("tree-orphan", 1.00m);
        string directory = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        Directory.CreateDirectory(directory);
        BudgetLedgerEntry orphan = new("job-orphan", "builder", Cap: 0.50m, Cost: null, DateTimeOffset.UtcNow, FinishedAt: null);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "job-orphan.json"),
            JsonSerializer.Serialize(orphan, ClaustrumJsonContext.Default.BudgetLedgerEntry),
            TestContext.Current.CancellationToken);

        BudgetLedgerState state = await BudgetLedger.ReadAsync(platform, tree.TreeId);

        BudgetLedgerRow row = Assert.Single(state.Rows);
        Assert.Equal(BudgetEntryState.Abandoned, row.State);
        Assert.Equal(0m, state.Reserved);
    }

    [Fact]
    public async Task CorruptEntryFileIsSkippedNotFatalAsync()
    {
        JobTreeBudget tree = new("tree-corrupt", 1.00m);
        string directory = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "broken.json"), "{ not json", TestContext.Current.CancellationToken);

        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-good", "builder", requestedCap: null);

        Assert.True(admission.Admitted);
        Assert.Equal(1.00m, admission.EffectiveCap);

        await admission.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task ExhaustedTreeAloneRefusesWithNothingLeftAsync()
    {
        JobTreeBudget tree = new("tree-refuse-a", 1.00m);
        BudgetAdmission first = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 1.00m);
        await first.Reservation!.CompleteAsync(1.00m, ran: true);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-2", "builder", requestedCap: null);

        Assert.False(refused.Admitted);
        Assert.Null(refused.Reservation);
        Assert.EndsWith("nothing left for role 'builder'", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExhaustedTreeWithALiveSiblingNamesTheWaitAsync()
    {
        JobTreeBudget tree = new("tree-refuse-b", 1.00m);
        BudgetAdmission holder = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 1.00m);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-2", "builder", requestedCap: null);

        Assert.False(refused.Admitted);
        Assert.EndsWith(
            "nothing left for role 'builder' while 1 running job(s) hold $1.00 — wait for one to finish",
            refused.Reason, StringComparison.Ordinal);

        await holder.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitCapAboveTheRemainderNamesTheOverageAsync()
    {
        JobTreeBudget tree = new("tree-refuse-c", 0.10m);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.50m);

        Assert.False(refused.Admitted);
        Assert.EndsWith("--budget 0.50 exceeds it", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareSliceThatFloorsToZeroNamesTheDivisionAndAnExplicitAlternativeAsync()
    {
        JobTreeBudget tree = new("tree-refuse-d", 0.05m, Share: 10);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: null);

        Assert.False(refused.Admitted);
        Assert.EndsWith(
            "the slice for role 'builder' ($0.05 / 10) rounds to $0.00 — pass --budget (at most $0.05) to claim an explicit slice",
            refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitCapThatFloorsToZeroNamesItselfAsync()
    {
        JobTreeBudget tree = new("tree-refuse-e", 1.00m);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.004m);

        Assert.False(refused.Admitted);
        Assert.EndsWith("--budget 0.004 rounds to $0.00", refused.Reason, StringComparison.Ordinal);
    }

    // The remaining<=0 check runs before the explicit-cap check, so a small --budget is no escape
    // hatch once the tree itself is spent.
    [Fact]
    public async Task ExhaustedTreeRefusesEvenASmallExplicitBudgetAsync()
    {
        JobTreeBudget tree = new("tree-refuse-f", 1.00m);
        BudgetAdmission first = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 1.00m);
        await first.Reservation!.CompleteAsync(1.00m, ran: true);

        BudgetAdmission refused = await BudgetLedger.AdmitAsync(platform, tree, "job-2", "builder", requestedCap: 0.01m);

        Assert.False(refused.Admitted);
        Assert.EndsWith("nothing left for role 'builder'", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("--budget", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareOfThreeFloorsToTheNearestCentAsync()
    {
        JobTreeBudget tree = new("tree-round-a", 2.00m, Share: 3);

        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: null);

        Assert.Equal(0.66m, admission.EffectiveCap);
        await admission.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task AnExplicitSubCentBudgetFloorsRatherThanRoundsAsync()
    {
        JobTreeBudget tree = new("tree-round-b", 1.00m);

        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.125m);

        Assert.Equal(0.12m, admission.EffectiveCap);
        await admission.Reservation!.DisposeAsync();
    }

    // The lock is never deleted (NOTES.md), so a stuck holder is exactly a held-open .lock file; the
    // 5s deadline is real wall-clock time here, not simulated.
    [Fact]
    public async Task AdmitTimesOutWhenTheLockStaysHeldAsync()
    {
        JobTreeBudget tree = new("tree-locked", 1.00m);
        string directory = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        Directory.CreateDirectory(directory);
        using FileStream heldLock = new(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: null));

        Assert.Contains(directory, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeekRemainingForAMissingTreeReturnsTheFullBudgetAndCreatesNothingAsync()
    {
        JobTreeBudget tree = new("tree-never-touched", 3.50m);

        decimal remaining = await BudgetLedger.PeekRemainingAsync(platform, tree);

        Assert.Equal(3.50m, remaining);
        Assert.False(Directory.Exists(BudgetLedger.DirectoryFor(platform, tree.TreeId)));
    }

    [Fact]
    public async Task PeekRemainingReflectsAnOpenReservationAsync()
    {
        JobTreeBudget tree = new("tree-peek", 2.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, "job-1", "builder", requestedCap: 0.75m);

        decimal remaining = await BudgetLedger.PeekRemainingAsync(platform, tree);

        Assert.Equal(1.25m, remaining);
        await admission.Reservation!.DisposeAsync();
    }

    [Fact]
    public async Task ReadAsyncReportsRunningAndDoneRowsWithTheirSumsAsync()
    {
        JobTreeBudget tree = new("tree-read", 3.00m);
        BudgetAdmission running = await BudgetLedger.AdmitAsync(platform, tree, "job-running", "builder", requestedCap: 1.00m);
        BudgetAdmission toFinish = await BudgetLedger.AdmitAsync(platform, tree, "job-done", "builder", requestedCap: 0.30m);
        await toFinish.Reservation!.CompleteAsync(0.30m, ran: true);

        BudgetLedgerState state = await BudgetLedger.ReadAsync(platform, tree.TreeId);

        Assert.Equal(2, state.Rows.Length);
        Assert.Equal(BudgetEntryState.Running, state.Rows.Single(row => row.Entry.JobId == "job-running").State);
        Assert.Equal(BudgetEntryState.Done, state.Rows.Single(row => row.Entry.JobId == "job-done").State);
        Assert.Equal(0.30m, state.Spent);
        Assert.Equal(1.00m, state.Reserved);

        await running.Reservation!.DisposeAsync();
    }

    [Theory]
    [InlineData("tree-42", "tree-42")]
    [InlineData("  tree-42  ", "tree-42")]
    public void TreeIdForTrimsTheEnvironmentVariable(string raw, string expected)
    {
        FakePlatform fresh = new();
        fresh.EnvironmentVariables["CLAUSTRUM_PARENT_JOB"] = raw;

        Assert.Equal(expected, BudgetLedger.TreeIdFor(fresh));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TreeIdForIsNullWhenTheVariableIsBlank(string raw)
    {
        FakePlatform fresh = new();
        fresh.EnvironmentVariables["CLAUSTRUM_PARENT_JOB"] = raw;

        Assert.Null(BudgetLedger.TreeIdFor(fresh));
    }

    [Fact]
    public void TreeIdForIsNullWhenTheVariableIsAbsent() => Assert.Null(BudgetLedger.TreeIdFor(new FakePlatform()));

    [Fact]
    public void DirectoryForSanitizesPathSeparatorsSoTheyCannotNestDirectories()
    {
        string directory = BudgetLedger.DirectoryFor(platform, "a/b\\c");

        Assert.Equal("a_b_c", Path.GetFileName(directory));
    }

    // A bare run of dots would otherwise name the `budget/` root itself (".") or the home above it
    // ("..") instead of a tree — Sanitize keeps dots (legal in a real job id), so this collapse is a
    // second, explicit step for the all-dots case only.
    [Theory]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    [InlineData("...", "___")]
    public void DirectoryForCollapsesAnAllDotsIdSoItCannotEscapeBudget(string treeId, string expectedName)
    {
        string directory = BudgetLedger.DirectoryFor(platform, treeId);

        Assert.Equal(expectedName, Path.GetFileName(directory));
        Assert.Equal(Path.Combine(home, "budget"), Path.GetDirectoryName(directory));
    }

    [Fact]
    public void DirectoryForLeavesARealisticIdsDotsAlone()
    {
        string directory = BudgetLedger.DirectoryFor(platform, "20260921-160924-6e3a9f88");

        Assert.Equal("20260921-160924-6e3a9f88", Path.GetFileName(directory));
    }

    [Fact]
    public void ResolveHomeAndRootUseClaustrumHomeWhenSet()
    {
        Assert.Equal(home, JobDirectory.ResolveHome(platform));
        Assert.Equal(Path.Combine(home, "jobs"), JobDirectory.ResolveRoot(platform));
    }

    [Fact]
    public void ResolveHomeAndRootFallBackToDotClaustrumUnderTheHomeDirectoryWhenUnset()
    {
        FakePlatform bare = new() { HomeDirectory = "/home/nobody" };

        Assert.Equal(Path.Combine("/home/nobody", ".claustrum"), JobDirectory.ResolveHome(bare));
        Assert.Equal(Path.Combine("/home/nobody", ".claustrum", "jobs"), JobDirectory.ResolveRoot(bare));
    }

    private async Task<BudgetLedgerEntry> ReadEntryAsync(string treeId, string jobId)
    {
        string path = Path.Combine(BudgetLedger.DirectoryFor(platform, treeId), $"{jobId}.json");
        string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize(text, ClaustrumJsonContext.Default.BudgetLedgerEntry)!;
    }
}
