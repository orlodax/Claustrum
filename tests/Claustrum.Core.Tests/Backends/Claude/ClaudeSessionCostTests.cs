using System.Globalization;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Claude;

// #127 finding 2 (NOTES.md "Final review of #127"): `--resume` restores the session's running total into `total_cost_usd`, so a continuation is
// charged its total minus the HIGHEST `raw.total_cost_usd` any receipt in the store records for that session id. The receipts here are
// written the way Runner writes them; the hook is driven directly, so nothing needs claude.
public sealed class ClaudeSessionCostTests : IDisposable
{
    private const string Session = "sess-cost";

    private readonly string jobsRoot = Directory.CreateTempSubdirectory("claustrum-session-cost-").FullName;
    private readonly ClaudeBackend backend = new(platform: null!);

    public void Dispose() => Directory.Delete(jobsRoot, recursive: true);

    private static string Money(decimal usd) => usd.ToString(CultureInfo.InvariantCulture);

    // A receipt: `cost_usd` is what the job was charged, `raw.total_cost_usd` claude's own running total (the two differ for a continuation).
    private void Receipt(string jobId, string session, decimal charged, decimal? rawTotal)
    {
        string directory = Directory.CreateDirectory(Path.Combine(jobsRoot, jobId)).FullName;
        string raw = rawTotal is { } total ? $$""","raw":{"type":"result","session_id":"{{session}}","total_cost_usd":{{Money(total)}}}""" : "";
        File.WriteAllText(Path.Combine(directory, "result.json"), $$"""{"job_id":"{{jobId}}","session_id":"{{session}}","cost_usd":{{Money(charged)}}{{raw}}}""");
    }

    private PostRunContext Context(string jobId, string? resume = Session)
    {
        string directory = Directory.CreateDirectory(Path.Combine(jobsRoot, jobId)).FullName;
        return new PostRunContext(new ProcessSpec("claude", [], jobsRoot, [], []), resume, directory, null, false, "");
    }

    private static ParsedOutput Parsed(decimal? total, string? session = Session) => new("ok", session, total, new Usage(10, 20, 30, 40), [], null, false);

    private async Task<PostRunResult?> ChargeAsync(string jobId, decimal? total, string? session = Session)
    {
        IPostRunHook hook = backend;
        PostRunContext context = Context(jobId, session);
        PreRunBaseline? baseline = await hook.PreRunAsync(context, TestContext.Current.CancellationToken);
        return await hook.PostRunAsync(Parsed(total, session), context, baseline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AContinuationIsChargedItsTotalMinusTheSessionsLastReceiptsTotalAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.10m);

        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m);

        Assert.Equal(0.15m, result?.CostUsd);
        Assert.Null(result?.Warning);
    }

    // Two continuations in a row: the second subtracts the FIRST continuation's raw total (0.25), not the charge it was given (0.15), and not the original's.
    [Fact]
    public async Task AChainOfTwoContinuationsChargesEachOnlyWhatItAddedAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.10m);
        PostRunResult? first = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m);
        Receipt("20261016-000002-bbbbbbbb", Session, charged: first?.CostUsd ?? -1m, rawTotal: 0.25m);

        PostRunResult? second = await ChargeAsync("20261016-000003-cccccccc", total: 0.45m);

        Assert.Equal(0.15m, first?.CostUsd);
        Assert.Equal(0.20m, second?.CostUsd);
    }

    // Job ids sort by the second, so "the newest receipt" is not knowable from names: the highest total is the latest because totals never go down.
    [Fact]
    public async Task TheHighestTotalIsTheBaselineWhateverOrderTheJobIdsSortInAsync()
    {
        Receipt("20261016-000009-zzzzzzzz", Session, charged: 0.05m, rawTotal: 0.10m);
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.30m);

        PostRunResult? result = await ChargeAsync("20261016-000010-current0", total: 0.35m);

        Assert.Equal(0.05m, result?.CostUsd);
    }

    [Fact]
    public async Task UsageIsNeverSubtractedAndTheHookLeavesItToClaudeAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.10m);

        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m);

        Assert.Null(result?.Usage);
    }

    [Fact]
    public async Task AReceiptWithNoRawFallsBackToItsCostUsdAndOtherSessionsAreIgnoredAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.12m, rawTotal: null);
        Receipt("20261016-000001-bbbbbbbb", "another-session", charged: 9m, rawTotal: 9m);

        PostRunResult? result = await ChargeAsync("20261016-000002-cccccccc", total: 0.20m);

        Assert.Equal(0.08m, result?.CostUsd);
    }

    [Fact]
    public async Task TheCurrentJobsOwnReceiptIsNotItsBaselineAsync()
    {
        Receipt("20261016-000002-bbbbbbbb", Session, charged: 0m, rawTotal: 5m);
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.10m);

        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m);

        Assert.Equal(0.15m, result?.CostUsd);
    }

    [Fact]
    public async Task NoReceiptForTheSessionChargesTheWholeRunningTotalWithAWarningAsync()
    {
        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m);

        Assert.Null(result?.CostUsd);
        Assert.Contains("whole running total", result?.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATotalBelowTheBaselineChargesTheWholeRunningTotalWithAWarningAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.50m, rawTotal: 0.50m);

        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.20m);

        Assert.Null(result?.CostUsd);
        Assert.Contains("lower than", result?.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshRunHasNoBaselineAndTheHookDoesNothingAsync()
    {
        IPostRunHook hook = backend;
        PostRunContext context = Context("20261016-000001-aaaaaaaa", resume: null);

        Assert.Null(await hook.PreRunAsync(context, TestContext.Current.CancellationToken));
        Assert.Null(await hook.PostRunAsync(Parsed(0.2m), context, baseline: null, TestContext.Current.CancellationToken));
    }

    // `--resume` keeps the id; a run that came back under another one did not continue the session, so no baseline applies to it.
    [Fact]
    public async Task ARunThatCameBackUnderAnotherSessionIdIsNotAdjustedAsync()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.10m, rawTotal: 0.10m);

        PostRunResult? result = await ChargeAsync("20261016-000002-bbbbbbbb", total: 0.25m, session: Session);
        IPostRunHook hook = backend;
        PostRunContext context = Context("20261016-000003-cccccccc");
        PreRunBaseline? baseline = await hook.PreRunAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(await hook.PostRunAsync(Parsed(0.25m, session: "someone-else"), context, baseline, TestContext.Current.CancellationToken));
        Assert.Equal(0.15m, result?.CostUsd);
    }

    // The cap is a per-run guard (measured: it compares THIS run's spend), so a resume's --max-budget-usd goes through as given.
    [Fact]
    public void AResumesMaxBudgetIsPassedUnchangedWhateverTheSessionAlreadyCost()
    {
        Receipt("20261016-000001-aaaaaaaa", Session, charged: 0.40m, rawTotal: 0.40m);
        ResolvedRun run = new(
            Role: new ResolvedRole("builder", "system body", "claude", "sonnet", "high", new PermissionPolicy(PermissionLevel.Full, []), Blind: false, HasReport: true),
            Brief: "go on",
            Cwd: "/repo",
            BudgetUsd: 0.07m,
            ResumeSession: Session,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: Path.Combine(jobsRoot, "20261016-000002-bbbbbbbb"),
            Env: []);

        ProcessSpec spec = new ClaudeBackend(new Testing.FakePlatform()).Build(run);

        Assert.Equal("0.07", spec.Args[Array.IndexOf(spec.Args, "--max-budget-usd") + 1]);
    }
}
