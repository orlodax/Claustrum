using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Model;
using Claustrum.Core.Report;

namespace Claustrum.Core.Tests.Backends.Acp;

// Parse reads stdout.log, the agent's half of an ACP run, against the transcripts recorded from opencode 2.0.30 on 2026-10-16
// (tests/fixtures/acp/README.md). The rules are the ones NOTES.md "ACP backend: cost on a paid model" and "The ACP backend as built"
// measured; the synthetic cases live in AcpBackendParseRulesTests.
public sealed class AcpBackendParseTests
{
    private readonly AcpBackend backend = AcpLines.Backend();

    private ParsedOutput Parse(string stdout, string stderr = "", int exitCode = 0) => backend.Parse(stdout, stderr, exitCode);

    private ParsedOutput ParseFixture(string name, int exitCode = 0) => Parse(AcpLines.Fixture(name), exitCode: exitCode);

    private static string StopReasonOf(ParsedOutput parsed) => parsed.Raw?.GetProperty("stopReason").GetString() ?? "";

    // ---- a batch run that reported ----------------------------------------------------------------------------

    [Fact]
    public void ASuccessfulRunWithAReportIsNoErrorAndTheReplyIsEveryChunkOfTheTurnGlued()
    {
        ParsedOutput parsed = ParseFixture("opencode-success.jsonl");

        Assert.False(parsed.IsError);
        Assert.Equal("ses_eba6623ccffeLFV79J29JwzcYX", parsed.SessionId);
        Assert.Null(parsed.ErrorKind);
        Assert.Null(parsed.ErrorDetail);
        Assert.Empty(parsed.ReportedEdits);

        // 25 chunks of one message, concatenated exactly as the stream said them: an independent read of the fixture says the same text.
        Assert.Equal(AcpLines.MessageText("opencode-success.jsonl"), parsed.FinalMessage);
        Assert.StartsWith("Created `hello.txt` in the working directory", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(ReportStatus.Ok, ReportExtractor.Extract(parsed.FinalMessage).Status);
    }

    [Fact]
    public void ASuccessfulRunCostsTheLastUsageUpdateWhichOnAFreeModelIsZeroNotUnknown()
    {
        ParsedOutput parsed = ParseFixture("opencode-success.jsonl");

        Assert.Equal(0m, parsed.CostUsd);
    }

    [Fact]
    public void TheRunsUsageIsThePromptsOwnAndAFieldTheAgentDidNotReportStaysNull()
    {
        ParsedOutput parsed = ParseFixture("opencode-success.jsonl");

        Assert.Equal(new Usage(InputTokens: 864, OutputTokens: 538, CacheReadInputTokens: 39040, CacheCreationInputTokens: null), parsed.Usage);
    }

    [Fact]
    public void ARunOfThreeCompletedToolCallsReportsThreeAndKeepsTheAnswerAsRaw()
    {
        ParsedOutput parsed = ParseFixture("opencode-success.jsonl");

        Assert.Equal(3, parsed.ToolCallsCompleted);
        Assert.Equal("end_turn", StopReasonOf(parsed));
    }

    // opencode-report.jsonl is the one a code receipt cites by line: 84 updates, one usage_update at line 88, the answer at 89.
    [Fact]
    public void TheReportFixtureHasItsFenceItsSixToolCallsAndOneFailedAmongThemUncounted()
    {
        ParsedOutput parsed = ParseFixture("opencode-report.jsonl");

        Assert.False(parsed.IsError);
        Assert.Equal("ses_ebae19a6fffeUZBGAMpvI4yweB", parsed.SessionId);
        Assert.Equal(AcpLines.MessageText("opencode-report.jsonl"), parsed.FinalMessage);
        Assert.Equal(ReportStatus.Ok, ReportExtractor.Extract(parsed.FinalMessage).Status);
        Assert.Contains("\"status\": \"partial\"", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(5, parsed.ToolCallsCompleted);
        Assert.Equal(0m, parsed.CostUsd);
        Assert.Equal(new Usage(13860, 1127, 46464, null), parsed.Usage);
    }

    [Fact]
    public void TheReportFixtureHoldsItsUsageUpdateAtLine88AndItsAnswerAtLine89()
    {
        string[] lines = AcpLines.FixtureLines("opencode-report.jsonl");

        Assert.Equal(89, lines.Length);
        Assert.Contains("\"usage_update\"", lines[87], StringComparison.Ordinal);
        Assert.Contains("\"stopReason\":\"end_turn\"", lines[88], StringComparison.Ordinal);
    }

    // The price lands once, just before the answer: a run killed any time before line 89 shows a floor at best, and the parser must not charge it.
    [Theory]
    [InlineData(87)]
    [InlineData(88)]
    public void ARunCutOffBeforeItsAnswerIsNotPricedEvenWithTheUsageUpdateAlreadySeen(int lines)
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-report.jsonl", lines), exitCode: 137);

        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
        Assert.True(parsed.IsError);
        Assert.Equal("the agent ended before any prompt was answered", parsed.ErrorDetail);
    }

    [Fact]
    public void ARunCutOffMidMessageSaysItsLastWordsAsTheFinalMessage()
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-report.jsonl", 60), exitCode: 137);

        Assert.Equal(AcpLines.MessageText("opencode-report.jsonl", take: 60), parsed.FinalMessage);
        Assert.StartsWith("Created `hello.txt`, but the push was blocked", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("claustrum-report", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.True(parsed.IsError);
    }

    // Numeric ids (an older probe) are read by the shape of the result; the same transcript with the client's own string ids says the same.
    [Fact]
    public void ANumericIdTranscriptParsesLikeTheSameTranscriptWithTheClientsStringIds()
    {
        string[] lines = AcpLines.FixtureLines("opencode-report.jsonl");
        string[] rewritten = [.. lines];
        rewritten[0] = lines[0].Replace("\"id\":1,", "\"id\":\"init-1\",", StringComparison.Ordinal);
        rewritten[1] = lines[1].Replace("\"id\":2,", "\"id\":\"new-2\",", StringComparison.Ordinal);
        rewritten[3] = lines[3].Replace("\"id\":3,", "\"id\":\"config-3\",", StringComparison.Ordinal);
        rewritten[4] = lines[4].Replace("\"id\":4,", "\"id\":\"config-4\",", StringComparison.Ordinal);
        rewritten[88] = lines[88].Replace("\"id\":5,", "\"id\":\"prompt-5\",", StringComparison.Ordinal);
        Assert.NotEqual(lines[88], rewritten[88]);

        ParsedOutput numeric = Parse(string.Join('\n', lines));
        ParsedOutput strings = Parse(string.Join('\n', rewritten));

        Assert.Equal(strings.FinalMessage, numeric.FinalMessage);
        Assert.Equal(strings.SessionId, numeric.SessionId);
        Assert.Equal(strings.CostUsd, numeric.CostUsd);
        Assert.Equal(strings.Usage, numeric.Usage);
        Assert.Equal(strings.IsError, numeric.IsError);
        Assert.Equal(strings.ToolCallsCompleted, numeric.ToolCallsCompleted);
    }

    // ---- cancelled: a refused permission ends the turn there, and the session lives on -------------------------------

    [Fact]
    public void ACancelledTurnIsNotAnErrorAndSaysNothingItDidNotSay()
    {
        ParsedOutput parsed = ParseFixture("opencode-cancelled.jsonl");

        Assert.False(parsed.IsError);
        Assert.Equal("cancelled", StopReasonOf(parsed));
        Assert.Equal("", parsed.FinalMessage);
        Assert.Null(parsed.ErrorKind);
        Assert.Null(parsed.ErrorDetail);
        Assert.Equal("ses_ebae2086dffe3HwftVSZtv6CoC", parsed.SessionId);
    }

    [Fact]
    public void ACancelledTurnIsStillPricedAndCountsTheCallsThatCompletedNotTheRefusedOne()
    {
        ParsedOutput parsed = ParseFixture("opencode-cancelled.jsonl");

        Assert.Equal(0m, parsed.CostUsd);
        Assert.Equal(3, parsed.ToolCallsCompleted);
        Assert.Equal(new Usage(4502, 416, 25856, null), parsed.Usage);
    }

    // The agent's own request (line 47: session/request_permission) is neither a response nor a notification: it changes nothing here.
    [Fact]
    public void TheAgentsPermissionRequestIsNotAnAnswerAndChangesNoFigure()
    {
        string[] lines = AcpLines.FixtureLines("opencode-cancelled.jsonl");
        Assert.Contains("\"session/request_permission\"", lines[46], StringComparison.Ordinal);

        ParsedOutput withRequest = Parse(string.Join('\n', lines));
        ParsedOutput without = Parse(string.Join('\n', lines.Where((_, index) => index != 46)));

        Assert.Equal(without.CostUsd, withRequest.CostUsd);
        Assert.Equal(without.ToolCallsCompleted, withRequest.ToolCallsCompleted);
        Assert.Equal(without.IsError, withRequest.IsError);
    }

    // ---- several turns: the running total, the sum of the usages, the last turn's words --------------------------

    [Fact]
    public void ThreeTurnsCostTheLastUsageUpdateWhichIsTheSessionsRunningTotalNotTheirSum()
    {
        ParsedOutput parsed = ParseFixture("opencode-three-turns.jsonl");

        Assert.Equal(0.0018094075999999998m, parsed.CostUsd);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void ThreeTurnsUsageIsTheSumOfEachPromptsOwnAndASummandThatNeverReportedAFieldLeavesItNull()
    {
        ParsedOutput parsed = ParseFixture("opencode-three-turns.jsonl");

        Assert.Equal(new Usage(InputTokens: 8615 + 207 + 8612, OutputTokens: 4 + 1 + 1, CacheReadInputTokens: 8448, CacheCreationInputTokens: null), parsed.Usage);
    }

    [Fact]
    public void ThreeTurnsFinalMessageIsTheLastTurnsAloneAndTheirToolCallsAreNotCounted()
    {
        ParsedOutput parsed = ParseFixture("opencode-three-turns.jsonl");

        Assert.Equal("KIWI", parsed.FinalMessage);
        Assert.Equal("ses_ebac264f9ffeynEyOP134zTxuD", parsed.SessionId);
        Assert.Null(parsed.ToolCallsCompleted);
        Assert.Equal("end_turn", StopReasonOf(parsed));
    }

    // The recording ends with a model-not-found error (config-99) sent after the prompts were answered: a config error is a handshake
    // refusal only before the first prompt is answered, so this one changes nothing.
    [Fact]
    public void AConfigErrorAfterAPromptWasAnsweredIsNotAHandshakeRefusal()
    {
        string[] lines = AcpLines.FixtureLines("opencode-three-turns.jsonl");
        Assert.Contains("\"id\":\"config-99\"", lines[^1], StringComparison.Ordinal);

        ParsedOutput parsed = ParseFixture("opencode-three-turns.jsonl");

        Assert.Null(parsed.ErrorKind);
        Assert.Null(parsed.ErrorDetail);
        Assert.NotEqual(0m, parsed.CostUsd);
    }

    // Each cut is where a kill could land: it is the running total only while nothing of a later turn has been said.
    [Theory]
    [InlineData(8, "PINEAPPLE", "0.0007885149999999999")]
    [InlineData(12, "MANGO", "0.0010218935999999998")]
    [InlineData(16, "KIWI", "0.0018094075999999998")]
    public void TheCostIsTheTotalAfterTheLastAnsweredTurnWhenNothingElseCameAfterIt(int lines, string finalMessage, string cost)
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-three-turns.jsonl", lines));

        Assert.Equal(decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), parsed.CostUsd);
        Assert.Equal(finalMessage, parsed.FinalMessage);
        Assert.False(parsed.IsError);
    }

    // Line 13 is the third turn's first update: from then on the total is a floor, and a floor under-charges the ledger.
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void ATurnInFlightAfterTwoAnsweredOnesLeavesTheCostUnknown(int lines)
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-three-turns.jsonl", lines), exitCode: 137);

        Assert.Null(parsed.CostUsd);
        Assert.True(parsed.IsError);
        Assert.Equal(new Usage(8615 + 207, 4 + 1, 8448, null), parsed.Usage);
    }

    [Fact]
    public void ATurnCutOffMidReplyWithItsTextPendingSaysThatTextNotTheTurnBefore()
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-three-turns.jsonl", 14), exitCode: 137);

        Assert.Equal("KIWI", parsed.FinalMessage);
    }

    [Fact]
    public void ARunKilledWhileTheLastTurnIsInFlightButExitingCleanIsNotAnErrorByItself()
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-three-turns.jsonl", 14), exitCode: 0);

        Assert.False(parsed.IsError);
    }

    [Theory]
    [InlineData(6, "PINEAPPLE")]
    [InlineData(7, "PINEAPPLE")]
    public void ARunWhoseFirstPromptNeverAnsweredIsAnErrorWhateverItSaid(int lines, string pending)
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-three-turns.jsonl", lines));

        Assert.True(parsed.IsError);
        Assert.Equal("the agent ended before any prompt was answered", parsed.ErrorDetail);
        Assert.Equal(pending, parsed.FinalMessage);
        Assert.Null(parsed.CostUsd);
    }

    // ---- the provider refusing a prompt --------------------------------------------------------------------------

    [Fact]
    public void APromptAnsweredWithAJsonRpcErrorIsAFailureWhoseMessageIsTheFinalMessageAndTheDetail()
    {
        ParsedOutput parsed = ParseFixture("opencode-provider-error.jsonl");

        Assert.True(parsed.IsError);
        Assert.StartsWith("Internal error: 0 endpoints out of 3 requested are available matching your guardrail restrictions", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(parsed.FinalMessage, parsed.ErrorDetail);
        Assert.Equal("ses_ebac35b9fffeFShSy7FWHrruuV", parsed.SessionId);
    }

    // opencode's `data.errorName: provider.invalid-request` is not a name the shared classifier places (it never searches the message).
    [Fact]
    public void TheProviderErrorFixturesErrorNameIsClassedOtherAndNeverRetried()
    {
        ParsedOutput parsed = ParseFixture("opencode-provider-error.jsonl");

        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    // A refused prompt produced no turn and no usage_update: nothing to charge from the stream and nothing summed.
    [Fact]
    public void ARefusedPromptHasNoCostFromTheStreamAndNoUsage()
    {
        ParsedOutput parsed = ParseFixture("opencode-provider-error.jsonl");

        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
    }

    [Fact]
    public void TheFirstRefusedPromptAloneIsTheSameFailureAndNoToolWasRun()
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-provider-error.jsonl", 5));

        Assert.True(parsed.IsError);
        Assert.Equal(0, parsed.ToolCallsCompleted);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    // ---- session/load: the history is not this run's -------------------------------------------------------------

    [Fact]
    public void ALoadedSessionsReplayIsNotThisRunsReplyAndItsFinalMessageIsTheNewTurns()
    {
        ParsedOutput parsed = ParseFixture("opencode-load-replay.jsonl");

        Assert.False(parsed.IsError);
        Assert.Equal("PLUM", parsed.FinalMessage);
        Assert.Equal("ses_ebac264f9ffeynEyOP134zTxuD", parsed.SessionId);
    }

    // The session's total began before this run, so the stream cannot say what the run cost: the post-run export does (baseline subtracted).
    [Fact]
    public void ALoadedSessionHasNoStreamCostBecauseItsTotalIncludesWhatCameBefore()
    {
        ParsedOutput parsed = ParseFixture("opencode-load-replay.jsonl");

        Assert.Null(parsed.CostUsd);
    }

    [Fact]
    public void ALoadedRunsUsageIsTheNewPromptsAlone()
    {
        ParsedOutput parsed = ParseFixture("opencode-load-replay.jsonl");

        Assert.Equal(new Usage(200, 1, 8448, null), parsed.Usage);
    }

    [Fact]
    public void TheReplaysToolCallsAreNotCountedAsThisRunsAndTheRunCountsZero()
    {
        string[] lines = AcpLines.FixtureLines("opencode-load-replay.jsonl");
        string replayedTool = AcpLines.Tool("call_old", "completed", kind: "tool_call_update");
        string[] withHistory = [.. lines[..10], replayedTool, .. lines[10..]];

        ParsedOutput parsed = Parse(string.Join('\n', withHistory));

        Assert.Equal(0, parsed.ToolCallsCompleted);
        Assert.Equal("PLUM", parsed.FinalMessage);
    }

    // The run was killed during the replay (before the load answer): what the replay said is the previous session's turn, never this run's receipt.
    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void ARunKilledDuringTheReplayCarriesNoneOfTheHistoryAsItsReceipt(int lines)
    {
        ParsedOutput parsed = Parse(AcpLines.Head("opencode-load-replay.jsonl", lines), exitCode: 137);

        Assert.Equal("", parsed.FinalMessage);
        Assert.True(parsed.IsError);
        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // ---- a refusal in the handshake ------------------------------------------------------------------------------

    [Fact]
    public void ABadEffortIsRefusedInTheHandshakeAtNoCostAsAFailureOfKindOther()
    {
        ParsedOutput parsed = ParseFixture("opencode-effort-refused.jsonl");

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
        Assert.Equal(0m, parsed.CostUsd);
        Assert.Equal("Invalid params: effort not found: bogus", parsed.FinalMessage);
        Assert.Equal("Invalid params: effort not found: bogus", parsed.ErrorDetail);
        Assert.Equal("ses_eba69e473ffeYG40lW4pxzobaA", parsed.SessionId);
        Assert.Null(parsed.Usage);
    }

    // The recorded receipt of this very run says before_any_work: true. A cast's fallback applies only to a model_not_found/quota/auth run,
    // so Other is not retried; but the figure that decides "before any work" must hold.
    [Fact]
    public void ABadEffortRefusalIsBeforeAnyWorkByTheSharedPredicate()
    {
        ParsedOutput parsed = ParseFixture("opencode-effort-refused.jsonl");

        Assert.Equal(0, parsed.ToolCallsCompleted);
        Assert.True(ProviderErrors.BeforeAnyWork(parsed.ToolCallsCompleted, [], treeRead: true, firstTurnOnly: true, parsed.Steps));
    }

    [Fact]
    public void AnUnknownModelIsRefusedAtTheConfigOptionAsModelNotFoundAtNoCost()
    {
        string[] refusal = AcpLines.FixtureLines("opencode-effort-refused.jsonl")[..3];
        string modelRefused = AcpLines.Failure("config-3", -32602, "Invalid params: model not found: openrouter/no-such/model-xyz", new { providerId = "openrouter", modelId = "openrouter/no-such/model-xyz" });

        ParsedOutput parsed = Parse(AcpLines.Join([.. refusal, modelRefused]));

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Equal(0m, parsed.CostUsd);
        Assert.Equal("Invalid params: model not found: openrouter/no-such/model-xyz", parsed.FinalMessage);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // ---- a run killed mid-turn -------------------------------------------------------------------------------------

    [Fact]
    public void ALiveRunKilledByItsTimeoutMidTurnIsAFailureWithNoCostAndNoReceiptText()
    {
        ParsedOutput parsed = Parse(AcpLines.Fixture("opencode-cut-off.jsonl"), exitCode: 137);

        Assert.True(parsed.IsError);
        Assert.Equal("", parsed.FinalMessage);
        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
        Assert.Equal("the agent ended before any prompt was answered", parsed.ErrorDetail);
        Assert.Equal("ses_eba65ea7cffePk534oTcDXoQeD", parsed.SessionId);
        Assert.Null(parsed.Raw);
    }

    // The call that was running when the kill came counts: an unseen end errs towards "work was done", so no retry is made on its account.
    [Fact]
    public void AToolCallStillRunningAtTheKillCountsAsWork()
    {
        ParsedOutput parsed = Parse(AcpLines.Fixture("opencode-cut-off.jsonl"), exitCode: 137);

        Assert.Equal(1, parsed.ToolCallsCompleted);
        Assert.False(ProviderErrors.BeforeAnyWork(parsed.ToolCallsCompleted, [], treeRead: true, firstTurnOnly: true, parsed.Steps));
    }

    // ---- the fixtures themselves ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("opencode-success")]
    [InlineData("opencode-report")]
    [InlineData("opencode-cancelled")]
    [InlineData("opencode-three-turns")]
    [InlineData("opencode-provider-error")]
    [InlineData("opencode-load-replay")]
    [InlineData("opencode-effort-refused")]
    [InlineData("opencode-cut-off")]
    public void EveryRecordedLineOfBothHalvesIsOneJsonObject(string name)
    {
        foreach (string suffix in new[] { ".jsonl", ".client.jsonl" })
        {
            foreach (string line in AcpLines.FixtureLines(name + suffix))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            }
        }
    }
}
