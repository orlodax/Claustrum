using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;

namespace Claustrum.Core.Tests.Backends.Opencode;

// success.jsonl/error.jsonl are genuine opencode 2.0.12 captures (2026-09-22); error-v1.jsonl is the
// kept 1.18.31 capture for the fallback branch. Event names are `step_start`/`tool_use`/
// `step_finish`/`text`, snake_case — nothing resembling v1's `message.part.updated` exists (NOTES.md
// "The opencode and api backends, validated against real endpoints", item 1).
public sealed class OpencodeBackendParseTests
{
    private readonly OpencodeBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", name);

    // Item 5: `step_finish` never fires after the last assistant message, so this genuine capture is
    // 2 step_start against 1 step_finish — an incomplete stream, which reports no cost by rule rather
    // than the floor opencode actually billed (item 1's $0.0004988494 is what was emitted, not what
    // Parse is entitled to report).
    [Fact]
    public void SuccessJsonlEndsWithTheReportFenceAndReportsNoCostForAnIncompleteStream()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("ses_f35a1b513ffeMTNgfaAtjYaIl6", parsed.SessionId);
        Assert.Contains("claustrum-report", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.EndsWith("````", parsed.FinalMessage.TrimEnd(), StringComparison.Ordinal);
        Assert.Null(parsed.CostUsd);
        Assert.Null(parsed.Usage);
        Assert.Empty(parsed.ReportedEdits);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void RealErrorCaptureExtractsTheFlatMessageAndSessionId()
    {
        string stdout = File.ReadAllText(FixturePath("error.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("User not found.", parsed.FinalMessage);
        Assert.Equal("ses_f35ab8b0fffeeIZgPVQSfNAiFb", parsed.SessionId);
    }

    // Review finding: IsError used to be `exitCode != 0` alone, so opencode's own captured error
    // event on a zero exit reported RunStatus.Success with the error text as the final message.
    [Fact]
    public void AnErrorEventIsAFailureEvenWhenTheProcessExitsZero()
    {
        string stdout = File.ReadAllText(FixturePath("error.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.True(parsed.IsError);
        Assert.Equal("User not found.", parsed.FinalMessage);
    }

    // The kept v1.18.31 capture: `error.data.message`, not `error.message` — the fallback
    // ExtractErrorMessage still reads.
    [Fact]
    public void V1ErrorCaptureStillYieldsItsMessageViaTheFallback()
    {
        string stdout = File.ReadAllText(FixturePath("error-v1.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("Unexpected server error. Check server logs for details.", parsed.FinalMessage);
    }

    // NOTES.md item 11: opencode refuses an unpublished `#<effort>` variant client-side, before any
    // API call, as a surfaced `error` event — free to reproduce and never silently downgraded.
    [Fact]
    public void AProviderNoRouteEventIsAFailureWithItsMessage()
    {
        string stdout = /*lang=json,strict*/
            """{"type":"error","timestamp":1,"sessionID":"ses_x","error":{"type":"provider.no-route","message":"Variant unavailable for openrouter/deepseek/deepseek-v4-flash: max"}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("Variant unavailable for openrouter/deepseek/deepseek-v4-flash: max", parsed.FinalMessage);
    }

    // Partial text before the failure is kept, so the reason is never silently dropped.
    [Fact]
    public void TextProducedBeforeAnErrorEventIsKeptAlongsideTheReason()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"got this far"}}""",
            /*lang=json,strict*/ """{"type":"error","sessionID":"ses_1","error":{"type":"provider.auth","message":"boom","status":401}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.True(parsed.IsError);
        Assert.Contains("got this far", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("boom", parsed.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStdoutFallsBackToStderrAsFinalMessage()
    {
        ParsedOutput parsed = backend.Parse(stdout: "   ", stderr: "boom", exitCode: 1);

        Assert.Equal("boom", parsed.FinalMessage);
        Assert.True(parsed.IsError);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void MalformedLinesAreSkippedNotFatal()
    {
        string stdout = string.Join('\n',
        [
            "not json",
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"hi"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("hi", parsed.FinalMessage);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void ARepeatedTextEventForTheSamePartReplacesRatherThanAppendsItsText()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"partial"}}""",
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"final text"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("final text", parsed.FinalMessage);
    }

    [Fact]
    public void MultipleTextPartsAreConcatenatedInFirstSeenOrder()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p2","type":"text","text":"second"}}""",
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"first-late-arrival"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        // p2 was seen first in the stream, so its text comes first regardless of id ordering.
        Assert.Equal("secondfirst-late-arrival", parsed.FinalMessage);
    }

    [Fact]
    public void ToolUseEventsAreIgnored()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_1","part":{"id":"p1","type":"tool","tool":"write"}}""",
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"p2","type":"text","text":"kept"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("kept", parsed.FinalMessage);
    }

    // Item 1: each step's cost/tokens are its own, not a running total, so a complete stream sums
    // rather than overwrites — the arithmetic bug summation replaced.
    [Fact]
    public void ABalancedStreamSumsCostAndAllFourTokenCounts()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"step_start","sessionID":"ses_1","part":{"id":"s1","type":"step-start"}}""",
            /*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_1","part":{"id":"f1","type":"step-finish","cost":0.001,"tokens":{"input":50,"output":5,"reasoning":1,"cache":{"read":2,"write":1}}}}""",
            /*lang=json,strict*/ """{"type":"step_start","sessionID":"ses_1","part":{"id":"s2","type":"step-start"}}""",
            /*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_1","part":{"id":"f2","type":"step-finish","cost":0.002,"tokens":{"input":70,"output":7,"reasoning":0,"cache":{"read":3,"write":0}}}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(0.003m, parsed.CostUsd);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(120, parsed.Usage!.InputTokens);
        Assert.Equal(12, parsed.Usage.OutputTokens);
        Assert.Equal(5, parsed.Usage.CacheReadInputTokens);
        Assert.Equal(1, parsed.Usage.CacheCreationInputTokens);
    }

    // A repeated `step_finish` for the same part.id must count once, not be billed twice.
    [Fact]
    public void ARepeatedStepFinishForTheSamePartIdCountsOnce()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"step_start","sessionID":"ses_1","part":{"id":"s1","type":"step-start"}}""",
            /*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_1","part":{"id":"f1","type":"step-finish","cost":0.0005,"tokens":{"input":10,"output":1}}}""",
            /*lang=json,strict*/ """{"type":"step_finish","sessionID":"ses_1","part":{"id":"f1","type":"step-finish","cost":0.0005,"tokens":{"input":10,"output":1}}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(0.0005m, parsed.CostUsd);
        Assert.Equal(10, parsed.Usage!.InputTokens);
    }
}
