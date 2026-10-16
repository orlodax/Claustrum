using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Config;
using Claustrum.Core.Tests.Testing;
using static Claustrum.Core.Tests.Backends.Acp.AcpLines;

namespace Claustrum.Core.Tests.Backends.Acp;

// #130, "What a run is charged": after every run with a session the hook reads the session's total outside the stream (for opencode, `session
// export`) and charges it, less the total read before a resumed run began; the stream's settled figure is only the fallback, and a run whose cost
// cannot be known is charged the cap. The harness is a scripted fake, so every branch and its exact warning is reached without a process.
public sealed class AcpPostRunHookTests
{
    private const string Cap = "cost unknown, charged the cap";

    private static AcpTotals Totals(params AcpSessionTotal?[] totals) => new(totals);

    // One scripted harness and the backend wrapped around it.
    private sealed class AcpTotals
    {
        public AcpTotals(AcpSessionTotal?[] totals, string name = "fake", bool loads = true)
        {
            Harness = new FakeAcpHarness(name, totals) { LoadsSessions = loads };
            Backend = new AcpBackend(new FakePlatform(), Harness);
            Hook = Backend;
        }

        public FakeAcpHarness Harness { get; }

        public AcpBackend Backend { get; }

        public IPostRunHook Hook { get; }
    }

    private static PostRunContext Context(string stdout = "", string? resume = null) => new(
        new ProcessSpec("fake-agent", [], "/repo", [], []),
        resume,
        "/job",
        new BackendConfig("/bin/fake-agent"),
        EnvPassthroughAll: false,
        stdout);

    private static string Stream(string session = Session, decimal? cost = 0.20m) =>
        Join([.. Handshake(session), Message("hi", session: session), .. cost is { } amount ? new[] { Cost(amount, session: session) } : [], Answered("prompt-5")]);

    private static ParsedOutput Parsed(string? session = Session, decimal? cost = 0.20m) => new("hi", session, cost, null, [], null, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- before the run -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AFreshRunTakesNoBaselineAndReadsNothingAsync(string? resume)
    {
        AcpTotals acp = Totals(new AcpSessionTotal(1m, null));

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(Context(resume: resume), Ct);

        Assert.Null(baseline);
        Assert.Empty(acp.Harness.Reads);
    }

    [Fact]
    public async Task AHarnessThatCannotLoadASessionTakesNoBaselineEitherAsync()
    {
        AcpTotals acp = new([new AcpSessionTotal(1m, null)], loads: false);

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(Context(resume: "ses_old"), Ct);

        Assert.Null(baseline);
        Assert.Empty(acp.Harness.Reads);
    }

    [Fact]
    public async Task AResumedSessionsBaselineIsTheTotalReadBeforeTheRunAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.10m, null));
        PostRunContext context = Context(resume: "ses_old");

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(context, Ct);

        Assert.Equal(new PreRunBaseline(0.10m, null, null), baseline);
        (string sessionId, PostRunContext readWith, CancellationToken token) = Assert.Single(acp.Harness.Reads);
        Assert.Equal("ses_old", sessionId);
        Assert.Same(context, readWith);
        Assert.Equal(Ct, token);
    }

    [Fact]
    public async Task AHarnessWithNoWayToReadTheTotalGivesAFailedBaselineNotAZeroOneAsync()
    {
        AcpTotals acp = Totals((AcpSessionTotal?)null);

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(Context(resume: "ses_old"), Ct);

        Assert.Equal(new PreRunBaseline(null, null, "the harness has no way to read a session's total"), baseline);
    }

    [Fact]
    public async Task AFailedReadBeforeTheRunIsTheBaselinesFailureAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(null, "boom"));

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(Context(resume: "ses_old"), Ct);

        Assert.Equal(new PreRunBaseline(null, null, "boom"), baseline);
    }

    // A total with neither a cost nor a failure is a failure, never a $0 baseline that would charge the whole history.
    [Fact]
    public async Task ATotalThatCarriesNoCostIsAFailureNotAZeroBaselineAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(null, null));

        PreRunBaseline? baseline = await acp.Hook.PreRunAsync(Context(resume: "ses_old"), Ct);

        Assert.Equal(new PreRunBaseline(null, null, "it carries no cost"), baseline);
    }

    // ---- after the run: what is charged ------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshRunIsChargedTheSessionsWholeTotalAndItsUsageIsLeftToParseAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.30m, null));
        PostRunContext context = Context(Stream());

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(), context, baseline: null, Ct);

        Assert.Equal(new PostRunResult(0.30m, null, null), result);
        (string sessionId, PostRunContext readWith, _) = Assert.Single(acp.Harness.Reads);
        Assert.Equal(Session, sessionId);
        Assert.Same(context, readWith);
    }

    // The stream's last usage_update says 0.20; the session's own total says 0.30: the total is the bill.
    [Fact]
    public async Task TheTotalReadOutsideTheStreamWinsOverTheStreamsOwnFigureAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.30m, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: 0.20m), Context(Stream(cost: 0.20m)), baseline: null, Ct);

        Assert.Equal(0.30m, result?.CostUsd);
    }

    [Fact]
    public async Task AResumedRunIsChargedOnlyWhatItAddedTheTotalAfterMinusBeforeAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.25m, null));
        PreRunBaseline baseline = new(0.10m, null, null);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_old", cost: null), Context(Stream("ses_old"), resume: "ses_old"), baseline, Ct);

        Assert.Equal(new PostRunResult(0.15m, null, null), result);
    }

    // The baseline belongs to the session that was resumed: a run that ended up on another one owes its whole total.
    [Fact]
    public async Task ABaselineOnlyAppliesToTheSessionThatWasReallyResumedAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.25m, null));
        PreRunBaseline baseline = new(0.10m, null, null);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_new"), Context(Stream("ses_new"), resume: "ses_old"), baseline, Ct);

        Assert.Equal(0.25m, result?.CostUsd);
        Assert.Equal("ses_new", Assert.Single(acp.Harness.Reads).SessionId);
    }

    [Fact]
    public async Task ATotalThatDidNotMoveIsAFreeRunNotARefundAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.10m, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_old", cost: null), Context(Stream("ses_old"), resume: "ses_old"), new PreRunBaseline(0.10m, null, null), Ct);

        Assert.Equal(new PostRunResult(0m, null, null), result);
    }

    // ---- after the run: when it cannot be known ---------------------------------------------------------------------

    [Fact]
    public async Task ATotalThatWentDownBetweenTheTwoReadsIsABadReadingAndTheCapIsChargedAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.05m, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_old", cost: null), Context(Stream("ses_old"), resume: "ses_old"), new PreRunBaseline(0.10m, null, null), Ct);

        Assert.Equal(new PostRunResult(null, null, $"acp-fake: the session's total went down between the two reads: {Cap}"), result);
    }

    [Fact]
    public async Task AResumedSessionWhoseBaselineCouldNotBeReadIsTheCapAndTheTotalIsNotEvenReadAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.25m, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_old", cost: null), Context(Stream("ses_old"), resume: "ses_old"), new PreRunBaseline(null, null, "boom"), Ct);

        Assert.Equal(new PostRunResult(null, null, $"acp-fake: the pre-run total of the resumed session could not be read (boom): {Cap}"), result);
        Assert.Empty(acp.Harness.Reads);
    }

    // A loaded session's total includes what it cost before: priced as a fresh run, the other session's whole history would be charged.
    [Fact]
    public async Task ALoadedRunWhoseStreamNamesAnotherSessionThanTheOneResumedIsTheCapAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.25m, null));
        string stream = Join([Initialized(), Message("history", session: "ses_other"), Loaded(), Configured("config-3"), Message("new", session: "ses_other"), Answered("prompt-4")]);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_other", cost: null), Context(stream, resume: "ses_old"), new PreRunBaseline(0.10m, null, null), Ct);

        Assert.Equal(new PostRunResult(null, null, $"acp-fake: the stream names another session than the one resumed: {Cap}"), result);
        Assert.Empty(acp.Harness.Reads);
    }

    [Fact]
    public async Task ALoadedRunThatKeptTheResumedSessionIsPricedByTheTotalsDifferenceAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.40m, null));
        string stream = Join([Initialized(), Message("history", session: "ses_old"), Loaded(), Configured("config-3"), Message("new", session: "ses_old"), Answered("prompt-4")]);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed("ses_old", cost: null), Context(stream, resume: "ses_old"), new PreRunBaseline(0.10m, null, null), Ct);

        Assert.Equal(0.30m, result?.CostUsd);
    }

    [Fact]
    public async Task WhenTheTotalCannotBeReadASettledStreamsFigureStandsWithAWarningAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(null, "no such session"));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: 0.20m), Context(Stream()), baseline: null, Ct);

        Assert.Equal(new PostRunResult(null, null, "acp-fake: session total unavailable (no such session): cost is the stream's last usage_update"), result);
    }

    [Fact]
    public async Task WhenTheTotalCannotBeReadAndTheStreamHasNoFigureEitherTheCapIsChargedAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(null, "no such session"));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: null), Context(Stream(cost: null)), baseline: null, Ct);

        Assert.Equal(new PostRunResult(null, null, $"acp-fake: session total unavailable (no such session): {Cap}"), result);
    }

    [Fact]
    public async Task AHarnessWithNoWayToReadATotalFallsBackTheSameWayAsync()
    {
        AcpTotals acp = Totals((AcpSessionTotal?)null);

        PostRunResult? withFigure = await acp.Hook.PostRunAsync(Parsed(cost: 0.20m), Context(Stream()), baseline: null, Ct);
        PostRunResult? without = await acp.Hook.PostRunAsync(Parsed(cost: null), Context(Stream(cost: null)), baseline: null, Ct);

        Assert.Equal("acp-fake: session total unavailable (the harness has no way to read a session's total): cost is the stream's last usage_update", withFigure?.Warning);
        Assert.Null(withFigure?.CostUsd);
        Assert.Equal($"acp-fake: session total unavailable (the harness has no way to read a session's total): {Cap}", without?.Warning);
        Assert.Null(without?.CostUsd);
    }

    [Fact]
    public async Task ATotalWithNeitherACostNorAFailureIsReportedAsCarryingNoCostAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(null, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: null), Context(Stream(cost: null)), baseline: null, Ct);

        Assert.Equal(new PostRunResult(null, null, $"acp-fake: session total unavailable (it carries no cost): {Cap}"), result);
    }

    // ---- when there is nothing to read ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ARunThatNamesNoSessionHasNothingToReadAsync(string? session)
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.30m, null));

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(session), Context(Stream()), baseline: null, Ct);

        Assert.Null(result);
        Assert.Empty(acp.Harness.Reads);
    }

    // Nothing was prompted, so nothing was billed: Parse's $0 stands and the export is not run.
    [Fact]
    public async Task ARefusalInTheHandshakeReadsNothingAndLeavesParsesZeroAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.30m, null));
        string refused = Join([Initialized(), Opened(), Failure("config-3", -32602, "model not found", new { modelId = "x" })]);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: 0m), Context(refused), baseline: null, Ct);

        Assert.Null(result);
        Assert.Empty(acp.Harness.Reads);
    }

    [Fact]
    public async Task EvenARunThatFailedAfterItsHandshakeIsReadBecauseItMayHaveCostSomethingAsync()
    {
        AcpTotals acp = Totals(new AcpSessionTotal(0.02m, null));
        string failed = Join([.. Handshake(), Message("half"), Failure("prompt-5", -32603, "provider died")]);

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: null), Context(failed), baseline: null, Ct);

        Assert.Equal(0.02m, result?.CostUsd);
    }

    [Fact]
    public async Task TheWarningsPrefixNamesTheBackendNotAlwaysOpencodeAsync()
    {
        AcpTotals acp = new([null], name: "gemini");

        PostRunResult? result = await acp.Hook.PostRunAsync(Parsed(cost: null), Context(Stream(cost: null)), baseline: null, Ct);

        Assert.StartsWith("acp-gemini: ", result?.Warning, StringComparison.Ordinal);
        Assert.Equal("acp-gemini", acp.Backend.Name);
    }
}
