using System.Globalization;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Acp;
using Claustrum.Core.Model;
using static Claustrum.Core.Tests.Backends.Acp.AcpLines;

namespace Claustrum.Core.Tests.Backends.Acp;

// The rules of AcpBackend.Parse one at a time, each on the smallest transcript that shows it. The recorded transcripts
// (AcpBackendParseTests) show that the rules agree with what opencode did; these show each rule still holds when a recording
// does not happen to exercise it.
public sealed class AcpBackendParseRulesTests
{
    private readonly AcpBackend backend = Backend();

    private ParsedOutput Parse(IEnumerable<string> lines, string stderr = "", int exitCode = 0) => backend.Parse(string.Join('\n', lines), stderr, exitCode);

    private static string[] Turn(string text, int prompt = 5, decimal? cost = 0.5m, string messageId = "msg_1", object? usage = null) =>
        [Message(text, messageId), .. cost is { } amount ? new[] { Cost(amount) } : [], Answered($"prompt-{prompt}", usage: usage)];

    private static string[] Transcript(params string[][] turns) => [.. Handshake(), .. turns.SelectMany(turn => turn)];

    private const string Fence = "\n```claustrum-report\n{\"summary\":\"done\"}\n```";

    // ---- the final message ------------------------------------------------------------------------------------------

    [Fact]
    public void ChunksOfOneMessageAreGluedWithNothingBetweenThem()
    {
        ParsedOutput parsed = Parse(Transcript([Message("Hel"), Message("lo, "), Message("world"), Cost(0.1m), Answered("prompt-5")]));

        Assert.Equal("Hello, world", parsed.FinalMessage);
    }

    // One before a tool call and one after, each its own message: glued they read "I'll match it.Created ...".
    [Fact]
    public void DistinctMessagesOfOneTurnAreSeparatedByABlankLine()
    {
        ParsedOutput parsed = Parse(Transcript([Message("I'll match it.", "msg_1"), Tool("c1", "completed"), Message("Created it.", "msg_2"), Cost(0.1m), Answered("prompt-5")]));

        Assert.Equal("I'll match it.\n\nCreated it.", parsed.FinalMessage);
    }

    [Fact]
    public void ChunksWithNoMessageIdAreOneMessageAndOneWithAnIdAfterThemIsAnother()
    {
        string anonymous = Frame(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = Session, update = new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "ab" } } } });

        ParsedOutput parsed = Parse(Transcript([anonymous, anonymous, Message("c", "msg_9"), Answered("prompt-5")]));

        Assert.Equal("abab\n\nc", parsed.FinalMessage);
    }

    [Fact]
    public void ThoughtsUserEchoesAndNonTextContentAreNotTheReply()
    {
        string image = Update(Session, new { sessionUpdate = "agent_message_chunk", messageId = "msg_1", content = new { type = "image", data = "AAAA" } });

        ParsedOutput parsed = Parse(Transcript([Thought("let me think"), User("echo of the brief"), image, Message("the reply"), Plan(), Commands(), Answered("prompt-5")]));

        Assert.Equal("the reply", parsed.FinalMessage);
    }

    [Fact]
    public void TheLastAnsweredTurnIsTheFinalMessageNotTheTurnsRunTogether()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("first", 5, 0.1m), Turn("second", 6, 0.2m)));

        Assert.Equal("second", parsed.FinalMessage);
    }

    [Fact]
    public void AnEarlierTurnThatCarriedTheReportStaysTheFinalMessageWhenTheLastOneHasNone()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("all done" + Fence, 5, 0.1m), Turn("anything else?", 6, 0.2m)));

        Assert.Equal("all done" + Fence, parsed.FinalMessage);
    }

    [Fact]
    public void TheLastTurnWinsWhenItCarriesTheReportToo()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("first" + Fence, 5, 0.1m), Turn("second" + Fence, 6, 0.2m)));

        Assert.StartsWith("second", parsed.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnCutOffWithTextSaysItsOwnWordsRatherThanTheTurnBefore()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("first answer", 5, 0.1m), [Message("second, half-wri", "msg_2")]), exitCode: 137);

        Assert.Equal("second, half-wri", parsed.FinalMessage);
    }

    // Claude's rule: the turn that carried the report is the receipt even when a later, cut-off turn spoke last.
    [Fact]
    public void ACutOffTurnWithNoFenceDoesNotHideAnEarlierTurnsReport()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("reported" + Fence, 5, 0.1m), [Message("and then I was killed", "msg_2")]), exitCode: 137);

        Assert.Equal("reported" + Fence, parsed.FinalMessage);
    }

    [Fact]
    public void ACutOffTurnWithNoTextLeavesTheLastAnsweredTurnsAsTheFinalMessage()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("first answer", 5, 0.1m), [Thought("pondering", "msg_2:reasoning:0"), Tool("c1", "in_progress")]), exitCode: 137);

        Assert.Equal("first answer", parsed.FinalMessage);
    }

    [Fact]
    public void AJsonRpcErrorIsSaidAfterTheTextTheTurnHadAndIsBothTheMessageAndTheDetail()
    {
        ParsedOutput parsed = Parse(Transcript([Message("partial work"), Failure("prompt-5", -32603, "the provider is down")]));

        Assert.Equal("partial work\n\nthe provider is down", parsed.FinalMessage);
        Assert.Equal("the provider is down", parsed.ErrorDetail);
        Assert.True(parsed.IsError);
    }

    [Fact]
    public void AnErrorWithNoMessageIsSaidInTheWordsOfTheClient()
    {
        string bare = Frame(new { jsonrpc = "2.0", id = "prompt-5", error = new { code = -32603 } });

        ParsedOutput parsed = Parse(Transcript([bare]));

        Assert.True(parsed.IsError);
        Assert.Equal("the agent answered with an error", parsed.FinalMessage);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    [InlineData("max_turn_requests")]
    public void AStopReasonThatCutTheTurnIsAnErrorSaidAfterItsText(string stopReason)
    {
        ParsedOutput parsed = Parse(Transcript([Message("I will not"), Cost(0.1m), Answered("prompt-5", stopReason)]));

        Assert.True(parsed.IsError);
        Assert.Equal($"I will not\n\nthe agent stopped: {stopReason}", parsed.FinalMessage);
        Assert.Equal($"the agent stopped: {stopReason}", parsed.ErrorDetail);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    [Fact]
    public void ATurnThatStoppedWithNoTextSaysOnlyWhyItStopped()
    {
        ParsedOutput parsed = Parse(Transcript([Answered("prompt-5", "max_tokens")]));

        Assert.Equal("the agent stopped: max_tokens", parsed.FinalMessage);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("end_turn")]
    [InlineData("some_future_reason")]
    public void AnyOtherStopReasonIsNotAFailureAndAddsNothingToTheMessage(string stopReason)
    {
        ParsedOutput parsed = Parse(Transcript([Message("fine"), Cost(0.1m), Answered("prompt-5", stopReason)]));

        Assert.False(parsed.IsError);
        Assert.Equal("fine", parsed.FinalMessage);
        Assert.Null(parsed.ErrorDetail);
        Assert.Null(parsed.ErrorKind);
    }

    [Fact]
    public void WithNoTextAtAllTheAgentsStderrSpeaks()
    {
        ParsedOutput parsed = Parse(Transcript([Answered("prompt-5")]), stderr: "  opencode: something broke\n", exitCode: 0);

        Assert.Equal("opencode: something broke", parsed.FinalMessage);
    }

    [Fact]
    public void WhenTheProcessNeverSpokeJsonRpcItsRawStdoutIsTheMessageAndStderrComesFirst()
    {
        ParsedOutput onlyStdout = backend.Parse("opencode: command not found\n", "", 127);
        ParsedOutput both = backend.Parse("opencode: command not found\n", "the real reason", 127);

        Assert.True(onlyStdout.IsError);
        Assert.Equal("opencode: command not found", onlyStdout.FinalMessage);
        Assert.Null(onlyStdout.ErrorDetail);
        Assert.Equal("the real reason", both.FinalMessage);
    }

    [Fact]
    public void ARunThatSaidNothingAtAllIsAnErrorWithAnEmptyMessageAndNoSession()
    {
        ParsedOutput parsed = backend.Parse("", "", 0);

        Assert.True(parsed.IsError);
        Assert.Equal("", parsed.FinalMessage);
        Assert.Null(parsed.SessionId);
        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
        Assert.Null(parsed.Raw);
    }

    // The live protocol is what turns "answered without any text" into a failure of the session; Parse itself calls it a clean turn.
    [Fact]
    public void ACleanTurnWithNoTextIsNotAnErrorToParse()
    {
        ParsedOutput parsed = Parse(Transcript([Cost(0.1m), Answered("prompt-5")]));

        Assert.False(parsed.IsError);
        Assert.Equal("", parsed.FinalMessage);
        Assert.Equal(0.1m, parsed.CostUsd);
    }

    [Fact]
    public void ANonZeroExitIsAnErrorWhateverTheTranscriptSays()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("fine")), exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("fine", parsed.FinalMessage);
    }

    [Fact]
    public void RawIsTheLastAnsweredTurnsResultOrItsError()
    {
        ParsedOutput answered = Parse(Transcript(Turn("a", 5, 0.1m), [Answered("prompt-6", "cancelled")]));
        ParsedOutput refused = Parse(Transcript([Failure("prompt-5", -32603, "no")]));

        Assert.Equal("cancelled", answered.Raw?.GetProperty("stopReason").GetString());
        Assert.Equal(-32603, refused.Raw?.GetProperty("code").GetInt32());
    }

    // ---- which session, and which updates are the run's --------------------------------------------------------------

    [Fact]
    public void TheSessionIsWhatSessionNewAnswersEvenIfAnUpdateNamedAnotherBeforeIt()
    {
        ParsedOutput parsed = Parse([Initialized(), Message("early", session: "ses_other"), Opened(session: "ses_real"), Configured("config-3"), Message("hi", session: "ses_real"), Answered("prompt-5")]);

        Assert.Equal("ses_real", parsed.SessionId);
        Assert.Equal("hi", parsed.FinalMessage);
    }

    [Fact]
    public void WithNoNewAnswerTheFirstUpdatesSessionIsTheRuns()
    {
        ParsedOutput parsed = Parse([Message("hi", session: "ses_u"), Cost(0.1m, session: "ses_u"), Answered("prompt-5")]);

        Assert.Equal("ses_u", parsed.SessionId);
    }

    // opencode streams its child sessions' updates too (`_meta "opencode/child-session-updates"`): a subagent's words are not the role's.
    [Fact]
    public void UpdatesOfAnotherSessionAreNeitherTheReplyNorTheCostNorAToolCall()
    {
        ParsedOutput parsed = Parse(Transcript(
            [
                Message("the subagent talking", session: "ses_child"),
                Cost(9.99m, session: "ses_child"),
                Tool("child_call", "completed", session: "ses_child"),
                Message("mine"),
                Tool("my_call", "completed"),
                Cost(0.25m),
                Answered("prompt-5"),
            ]));

        Assert.Equal("mine", parsed.FinalMessage);
        Assert.Equal(0.25m, parsed.CostUsd);
        Assert.Equal(1, parsed.ToolCallsCompleted);
        Assert.Equal(Session, parsed.SessionId);
    }

    [Fact]
    public void NothingTheAgentSaysBeforeTheSessionIsOpenBelongsToATurn()
    {
        ParsedOutput parsed = Parse([Initialized(), Message("stray"), Cost(7m), Tool("stray_call", "completed"), Opened(), Configured("config-3"), .. Turn("real", 5, cost: null)]);

        Assert.Equal("real", parsed.FinalMessage);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // The handshake has more answers after the session opens (the model, the mode): whatever the agent says before the last of them is no turn's either.
    [Fact]
    public void WhatTheAgentSaysBetweenOpeningTheSessionAndTheLastConfigAnswerBelongsToNoTurn()
    {
        ParsedOutput parsed = Parse(
        [
            Initialized(),
            Opened(),
            Message("welcome banner"),
            Thought("warming up"),
            Tool("early_call", "completed"),
            Cost(9m),
            Configured("config-3"),
            Configured("config-4"),
            .. Turn("the real reply", 5, cost: null),
        ]);

        Assert.Equal("the real reply", parsed.FinalMessage);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void JunkAndUnknownFramesBetweenTheRealOnesChangeNothing()
    {
        string[] lines =
        [
            .. Handshake(),
            "",
            "   ",
            "opencode: warning: something printed to stdout",
            "[1,2,3]",
            "42",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":null,"result":{}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":{"x":1},"result":{}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"session/update"}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_test","update":{"sessionUpdate":"a_future_kind"}}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_test","update":"not an object"}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"some/other/notification","params":{}}""",
            .. Turn("ok"),
            "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-9\",\"resu",
        ];

        ParsedOutput parsed = Parse(lines);

        Assert.False(parsed.IsError);
        Assert.Equal("ok", parsed.FinalMessage);
        Assert.Equal(0.5m, parsed.CostUsd);
    }

    [Fact]
    public void CarriageReturnsAtTheEndOfLinesAreHarmless()
    {
        ParsedOutput parsed = backend.Parse(string.Join("\r\n", Transcript(Turn("windows"))), "", 0);

        Assert.Equal("windows", parsed.FinalMessage);
        Assert.Equal(0.5m, parsed.CostUsd);
    }

    // An agent that does not hand the client's id back as sent is read by the shape of the result: a stop reason is a prompt's, a session id a new session's.
    [Fact]
    public void AnAnswerWhoseIdIsNotTheClientsIsReadByTheShapeOfItsResult()
    {
        ParsedOutput parsed = Parse(
        [
            Result("x", new { protocolVersion = 1 }),
            Result("y", new { sessionId = "ses_shape", configOptions = Array.Empty<object>() }),
            Message("shape", session: "ses_shape"),
            Cost(0.4m, session: "ses_shape"),
            Result("z", new { stopReason = "end_turn", usage = Tokens(input: 3) }),
        ]);

        Assert.Equal("shape", parsed.FinalMessage);
        Assert.Equal("ses_shape", parsed.SessionId);
        Assert.Equal(0.4m, parsed.CostUsd);
        Assert.Equal(new Usage(3, null, null, null), parsed.Usage);
    }

    // ---- usage ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AnAnswerThatReportsNoUsageLeavesTheRunsUsageNull()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("hi")));

        Assert.Null(parsed.Usage);
    }

    [Fact]
    public void UsageIsSummedFieldByFieldAndAFieldNoTurnReportedStaysNull()
    {
        ParsedOutput parsed = Parse(Transcript(
            Turn("a", 5, 0.1m, usage: Tokens(input: 10, output: 2)),
            Turn("b", 6, 0.2m, usage: Tokens(input: 5, cachedRead: 7)),
            Turn("c", 7, 0.3m, usage: Tokens(output: 1))));

        Assert.Equal(new Usage(InputTokens: 15, OutputTokens: 3, CacheReadInputTokens: 7, CacheCreationInputTokens: null), parsed.Usage);
    }

    [Fact]
    public void AReportedZeroIsAFigureAndNotTheSameAsNothingReported()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("a", usage: Tokens(input: 0))));

        Assert.Equal(new Usage(0, null, null, null), parsed.Usage);
    }

    [Fact]
    public void CacheWriteTokensAreTheCacheCreationFigure()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("a", usage: Tokens(cachedRead: 4, cachedWrite: 9))));

        Assert.Equal(new Usage(null, null, 4, 9), parsed.Usage);
    }

    [Fact]
    public void AnErrorAnswerAddsNoUsageToWhatTheTurnsBeforeItReported()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("a", 5, 0.1m, usage: Tokens(input: 4, output: 1)), [Failure("prompt-6", -32603, "boom")]));

        Assert.Equal(new Usage(4, 1, null, null), parsed.Usage);
    }

    [Fact]
    public void ATokenCountThatIsNotAnIntegerIsNotACount()
    {
        object odd = new Dictionary<string, object> { ["inputTokens"] = "many", ["outputTokens"] = 1.5, ["cachedReadTokens"] = 6 };

        ParsedOutput parsed = Parse(Transcript(Turn("a", usage: odd)));

        Assert.Equal(new Usage(null, null, 6, null), parsed.Usage);
    }

    // An empty usage object is "nothing reported", not a report of zero: it must not turn an unreported run into a Usage of four nulls.
    [Fact]
    public void AUsageObjectWithNoneOfTheFourCountsIsNotAReportAndTheRunsUsageStaysNull()
    {
        object unknownKeys = new Dictionary<string, object> { ["totalTokens"] = 12, ["thoughtTokens"] = 3 };

        ParsedOutput empty = Parse(Transcript(Turn("a", usage: Tokens())));
        ParsedOutput unknown = Parse(Transcript(Turn("a", usage: unknownKeys)));
        ParsedOutput both = Parse(Transcript(Turn("a", 5, 0.1m, usage: Tokens()), Turn("b", 6, 0.2m, usage: unknownKeys)));

        Assert.Null(empty.Usage);
        Assert.Null(unknown.Usage);
        Assert.Null(both.Usage);
    }

    [Fact]
    public void ATurnWithAnEmptyUsageObjectAddsNothingToATurnThatReported()
    {
        ParsedOutput reportedFirst = Parse(Transcript(Turn("a", 5, 0.1m, usage: Tokens(input: 10, output: 2)), Turn("b", 6, 0.2m, usage: Tokens())));
        ParsedOutput reportedLast = Parse(Transcript(Turn("a", 5, 0.1m, usage: Tokens()), Turn("b", 6, 0.2m, usage: Tokens(input: 10, output: 2))));

        Assert.Equal(new Usage(10, 2, null, null), reportedFirst.Usage);
        Assert.Equal(new Usage(10, 2, null, null), reportedLast.Usage);
    }

    [Fact]
    public void ATurnThatReportedOnlyOneCountKeepsTheOtherThreeNull()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("a", usage: Tokens(input: 7))));

        Assert.Equal(new Usage(InputTokens: 7, OutputTokens: null, CacheReadInputTokens: null, CacheCreationInputTokens: null), parsed.Usage);
    }

    // ---- cost ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void WhenATurnSendsSeveralUsageUpdatesTheLastIsTheTotal()
    {
        ParsedOutput parsed = Parse(Transcript([Message("x"), Cost(0.10m), Cost(0.30m), Answered("prompt-5")]));

        Assert.Equal(0.30m, parsed.CostUsd);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    public void TheCurrencyIsUsdIgnoringCase(string currency)
    {
        ParsedOutput parsed = Parse(Transcript([Message("x"), Cost(0.7m, currency), Answered("prompt-5")]));

        Assert.Equal(0.7m, parsed.CostUsd);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("")]
    public void AFigureInAnotherCurrencyIsNotACostAndTheRunIsUnpriced(string currency)
    {
        ParsedOutput parsed = Parse(Transcript([Message("x"), Cost(0.7m, currency), Answered("prompt-5")]));

        Assert.Null(parsed.CostUsd);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"sessionUpdate":"usage_update","used":1,"size":2}""")]
    [InlineData(/*lang=json,strict*/ """{"sessionUpdate":"usage_update","cost":{"currency":"USD"}}""")]
    [InlineData(/*lang=json,strict*/ """{"sessionUpdate":"usage_update","cost":{"amount":"1.5","currency":"USD"}}""")]
    [InlineData(/*lang=json,strict*/ """{"sessionUpdate":"usage_update","cost":null}""")]
    public void AUsageUpdateWithNoNumericUsdAmountPricesNothing(string update)
    {
        string line = "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"ses_test\",\"update\":" + update + "}}";

        ParsedOutput parsed = Parse(Transcript([Message("x"), line, Answered("prompt-5")]));

        Assert.Null(parsed.CostUsd);
    }

    [Fact]
    public void ATurnThatFailedPartWayWithoutAUsageUpdateDoesNotInheritTheTurnBeforesTotal()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("a", 5, 0.1m), [Message("half"), Failure("prompt-6", -32603, "provider died")]));

        Assert.Null(parsed.CostUsd);
        Assert.True(parsed.IsError);
    }

    [Fact]
    public void AFailedTurnThatDidSendItsUsageUpdateIsPriced()
    {
        ParsedOutput parsed = Parse(Transcript([Message("half"), Cost(0.2m), Failure("prompt-5", -32603, "provider died")]));

        Assert.Equal(0.2m, parsed.CostUsd);
    }

    // Anything the agent says for a turn after the last answer means a turn is under way, and the total is then a floor.
    [Theory]
    [InlineData("message")]
    [InlineData("thought")]
    [InlineData("tool")]
    [InlineData("cost")]
    [InlineData("plan")]
    public void AnythingOfALaterTurnAfterTheLastAnswerLeavesTheCostUnknown(string kind)
    {
        string late = kind switch
        {
            "message" => Message("and then"),
            "thought" => Thought("hmm"),
            "tool" => Tool("c9", "in_progress"),
            "cost" => Cost(0.9m),
            _ => Plan(),
        };

        ParsedOutput parsed = Parse(Transcript(Turn("done", 5, 0.5m), [late]));

        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.ToolCallsCompleted);
    }

    // available_commands_update can come at any time and says nothing of a turn.
    [Fact]
    public void ASessionLevelUpdateAfterTheLastAnswerDoesNotUnsettleTheCost()
    {
        ParsedOutput parsed = Parse(Transcript(Turn("done", 5, 0.5m), [Commands(), User("an echo")]));

        Assert.Equal(0.5m, parsed.CostUsd);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void ARefusalInTheHandshakeCostsZeroWhateverElseTheTranscriptHolds()
    {
        ParsedOutput parsed = Parse([Initialized(), Opened(), Failure("config-3", -32602, "no such model", new { modelId = "m" }), Cost(3m)]);

        Assert.Equal(0m, parsed.CostUsd);
    }

    // ---- tool calls ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryToolCallWhoseLastStatusIsNotFailedCountsWhateverItsOtherStatus()
    {
        ParsedOutput parsed = Parse(Transcript(
            [
                Tool("done", "completed"),
                Tool("failed", "failed", error: "denied"),
                Tool("running", "in_progress"),
                Tool("queued", "pending"),
                Tool("unsaid"),
                Message("x"),
                Cost(0.1m),
                Answered("prompt-5"),
            ]));

        Assert.Equal(4, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void ACallsLastStatusDecidesNotItsFirst()
    {
        ParsedOutput parsed = Parse(Transcript(
            [
                Tool("a", "pending"),
                Tool("a", "failed", kind: "tool_call_update"),
                Tool("b", "failed"),
                Tool("b", "completed", kind: "tool_call_update"),
                Tool("c"),
                Tool("c", kind: "tool_call_update", command: "ls"),
                Message("x"),
                Cost(0.1m),
                Answered("prompt-5"),
            ]));

        Assert.Equal(2, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void AToolFrameWithNoCallIdIsNotACall()
    {
        string noId = Update(Session, new { sessionUpdate = "tool_call", status = "completed" });

        ParsedOutput parsed = Parse(Transcript([noId, Message("x"), Cost(0.1m), Answered("prompt-5")]));

        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // After a second turn the human has spoken: the run is never "before any work" again, so it is not counted.
    [Fact]
    public void ToolCallsAreCountedForARunOfOneTurnOnly()
    {
        ParsedOutput one = Parse(Transcript([Tool("a", "completed"), .. Turn("x")]));
        ParsedOutput two = Parse(Transcript([Tool("a", "completed"), .. Turn("x", 5, 0.1m)], Turn("y", 6, 0.2m)));

        Assert.Equal(1, one.ToolCallsCompleted);
        Assert.Null(two.ToolCallsCompleted);
    }

    [Fact]
    public void ARunWhoseFirstPromptWasNeverAnsweredStillCountsWhatItHadRun()
    {
        ParsedOutput parsed = Parse(Transcript([Tool("a", "completed"), Tool("b", "failed"), Tool("c", "in_progress")]), exitCode: 137);

        Assert.Equal(2, parsed.ToolCallsCompleted);
    }

    // ---- errors and their kinds -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("provider.no-route", ErrorKind.ModelNotFound)]
    [InlineData("provider.model-not-found", ErrorKind.ModelNotFound)]
    [InlineData("provider.auth", ErrorKind.ProviderAuth)]
    [InlineData("provider.rate-limit", ErrorKind.ProviderQuota)]
    [InlineData("provider.credit-exhausted", ErrorKind.ProviderQuota)]
    [InlineData("provider.network", ErrorKind.ProviderTransport)]
    [InlineData("provider.invalid-request", ErrorKind.Other)]
    [InlineData("unknown", ErrorKind.Other)]
    public void APromptsErrorIsClassedByOpencodesErrorNameNeverByItsMessage(string errorName, ErrorKind expected)
    {
        string failure = Failure("prompt-5", -32603, "an authentication quota model overload message", new { errorName });

        ParsedOutput parsed = Parse(Transcript([failure]));

        Assert.True(parsed.IsError);
        Assert.Equal(expected, parsed.ErrorKind);
    }

    [Fact]
    public void APromptsErrorWithNoErrorNameIsOther()
    {
        ParsedOutput parsed = Parse(Transcript([Failure("prompt-5", -32603, "an authentication quota model overload message")]));

        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    // `data.modelId` on a config option's -32602 is the provider's ModelNotFound (#126), so a cast's fallback can take over; any other
    // refusal in the handshake is Other.
    [Fact]
    public void AConfigOptionNamingAnUnknownModelIsModelNotFound()
    {
        ParsedOutput parsed = Parse([Initialized(), Opened(), Failure("config-3", -32602, "Invalid params: model not found: x", new { providerId = "p", modelId = "x" })]);

        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Equal(0m, parsed.CostUsd);
        Assert.Equal("Invalid params: model not found: x", parsed.ErrorDetail);
    }

    [Fact]
    public void ARefusedConfigOptionWithNoModelIdIsOtherAndSoIsAnyEarlierStepEvenWithOne()
    {
        object withModel = new { modelId = "x" };

        ParsedOutput config = Parse([Initialized(), Opened(), Failure("config-3", -32602, "bad effort", new { effort = "bogus" })]);
        ParsedOutput init = Parse([Failure("init-1", -32602, "nope", withModel)]);
        ParsedOutput opened = Parse([Initialized(), Failure("new-2", -32602, "nope", withModel)]);
        ParsedOutput loaded = Parse([Initialized(), Failure("load-2", -32602, "session not found", withModel)]);

        Assert.Equal(ErrorKind.Other, config.ErrorKind);
        Assert.Equal(ErrorKind.Other, init.ErrorKind);
        Assert.Equal(ErrorKind.Other, opened.ErrorKind);
        Assert.Equal(ErrorKind.Other, loaded.ErrorKind);
        ParsedOutput[] all = [config, init, opened, loaded];
        Assert.All(all, parsed =>
        {
            Assert.True(parsed.IsError);
            Assert.Equal(0m, parsed.CostUsd);
        });
    }

    [Fact]
    public void TheFirstRefusalInTheHandshakeIsTheRunsAndALaterOneDoesNotReplaceIt()
    {
        ParsedOutput parsed = Parse([Initialized(), Opened(), Failure("config-3", -32602, "first", new { modelId = "x" }), Failure("config-4", -32602, "second")]);

        Assert.Equal("first", parsed.ErrorDetail);
        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
    }

    [Fact]
    public void ARefusalOfThePromptAfterAHandshakeThatWentThroughIsNotAHandshakeRefusal()
    {
        ParsedOutput parsed = Parse(Transcript([Failure("prompt-5", -32602, "Invalid params", new { modelId = "x" })]));

        Assert.True(parsed.IsError);
        Assert.NotEqual(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Null(parsed.CostUsd);
    }

    [Fact]
    public void ASessionTheAgentCouldNotOpenLeavesNoSessionToResume()
    {
        ParsedOutput parsed = Parse([Initialized(), Failure("new-2", -32603, "cannot open")]);

        Assert.Null(parsed.SessionId);
        Assert.Equal("cannot open", parsed.FinalMessage);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    // ---- load ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALoadedSessionIsNeverPricedFromTheStreamEvenWhenItsTurnWas()
    {
        ParsedOutput parsed = Parse([Initialized(), Message("history"), Loaded(), Configured("config-3"), .. Turn("new answer", 4, 2.5m)]);

        Assert.Equal("new answer", parsed.FinalMessage);
        Assert.Equal(Session, parsed.SessionId);
        Assert.Null(parsed.CostUsd);
    }

    [Fact]
    public void WhatALoadReplaysIsClearedWhenItsAnswerComes()
    {
        ParsedOutput parsed = Parse([Initialized(), Message("history"), Thought("old thought"), Tool("old", "completed"), Loaded(), Configured("config-3"), .. Turn("new", 4, 1m)]);

        Assert.Equal("new", parsed.FinalMessage);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    // ---- helpers about the numbers themselves ------------------------------------------------------------------------------

    [Fact]
    public void ACostIsReadAsAnExactDecimalNotAFloat()
    {
        string line = /*lang=json,strict*/ """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"ses_test","update":{"sessionUpdate":"usage_update","cost":{"amount":0.0007885149999999999,"currency":"USD"}}}}""";

        ParsedOutput parsed = Parse(Transcript([Message("x"), line, Answered("prompt-5")]));

        Assert.Equal(decimal.Parse("0.0007885149999999999", CultureInfo.InvariantCulture), parsed.CostUsd);
    }
}
