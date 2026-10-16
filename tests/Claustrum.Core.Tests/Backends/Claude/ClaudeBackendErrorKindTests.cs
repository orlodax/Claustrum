using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Claude;

// #126: error_kind and the completed-tool-call count from claude's result and stream. error.txt is the recorded 404 (an unknown model, $0).
public sealed class ClaudeBackendErrorKindTests
{
    private readonly ClaudeBackend backend = new(platform: null!);

    private const string Init = /*lang=json,strict*/ """{"type":"system","subtype":"init","session_id":"s1"}""";

    private static string Result(string extra) => /*lang=json,strict*/ $$"""{"type":"result","subtype":"success","session_id":"s1","total_cost_usd":0,"num_turns":1,{{extra}}}""";

    private static string Stream(params string[] lines) => string.Join('\n', lines);

    private static string ErrorFixtureStdout()
    {
        string[] lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", "error.txt"));
        return string.Join('\n', lines[(Array.IndexOf(lines, "## STDOUT") + 1)..Array.IndexOf(lines, "## STDERR")]);
    }

    [Fact]
    public void TheRecordedErrorResultIsModelNotFoundWithTheProvidersWords()
    {
        ParsedOutput parsed = backend.Parse(Stream(Init, ErrorFixtureStdout()), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void TheRecordedErrorAsAOneDocumentResultIsModelNotFoundButItsToolCallsAreNotCounted()
    {
        ParsedOutput parsed = backend.Parse(ErrorFixtureStdout(), stderr: "", exitCode: 1);

        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Null(parsed.ToolCallsCompleted);
    }

    [Theory]
    [InlineData(401, ErrorKind.ProviderAuth)]
    [InlineData(402, ErrorKind.ProviderQuota)]
    [InlineData(429, ErrorKind.ProviderQuota)]
    [InlineData(503, ErrorKind.ProviderTransport)]
    [InlineData(529, ErrorKind.ProviderTransport)]
    [InlineData(400, ErrorKind.Other)]
    public void AnApiErrorStatusOnTheResultDecidesTheKind(int status, ErrorKind expected)
    {
        string result = Result($"\"is_error\":true,\"api_error_status\":{status},\"result\":\"nope\"");

        ParsedOutput parsed = backend.Parse(Stream(Init, result), stderr: "", exitCode: 1);

        Assert.Equal(expected, parsed.ErrorKind);
        Assert.Equal("nope", parsed.ErrorDetail);
    }

    [Fact]
    public void AnApiErrorTerminalReasonWithNoStatusIsTransport()
    {
        string result = Result(""" "is_error":true,"terminal_reason":"api_error","result":"connection reset" """.Trim());

        Assert.Equal(ErrorKind.ProviderTransport, backend.Parse(Stream(Init, result), stderr: "", exitCode: 1).ErrorKind);
    }

    [Fact]
    public void AFailureWithNeitherStatusNorTerminalReasonIsOther()
    {
        string result = Result(""" "is_error":true,"result":"the role gave up" """.Trim());

        ParsedOutput parsed = backend.Parse(Stream(Init, result), stderr: "", exitCode: 1);

        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    [Fact]
    public void ASuccessHasNoKindAndNoDetail()
    {
        ParsedOutput parsed = backend.Parse(Stream(Init, Result(""" "is_error":false,"result":"ok" """.Trim())), stderr: "", exitCode: 0);

        Assert.Null(parsed.ErrorKind);
        Assert.Null(parsed.ErrorDetail);
    }

    // The assistant message's `error` places a failure whose result carries no status.
    [Theory]
    [InlineData("authentication_failed", ErrorKind.ProviderAuth)]
    [InlineData("billing_error", ErrorKind.ProviderQuota)]
    [InlineData("rate_limit", ErrorKind.ProviderQuota)]
    [InlineData("server_error", ErrorKind.ProviderTransport)]
    [InlineData("invalid_request", ErrorKind.Other)]
    public void TheAssistantMessagesErrorPlacesAResultThatHasNoStatus(string assistantError, ErrorKind expected)
    {
        string assistant = $$$"""{"type":"assistant","error":"{{{assistantError}}}","message":{"role":"assistant","content":[{"type":"text","text":"x"}]}}""";
        string result = Result(""" "is_error":true,"result":"x" """.Trim());

        Assert.Equal(expected, backend.Parse(Stream(Init, assistant, result), stderr: "", exitCode: 1).ErrorKind);
    }

    [Fact]
    public void TheResultsStatusWinsOverTheAssistantMessagesError()
    {
        string assistant = /*lang=json,strict*/ """{"type":"assistant","error":"authentication_failed","message":{"role":"assistant","content":[]}}""";
        string result = Result(""" "is_error":true,"api_error_status":402,"result":"x" """.Trim());

        Assert.Equal(ErrorKind.ProviderQuota, backend.Parse(Stream(Init, assistant, result), stderr: "", exitCode: 1).ErrorKind);
    }

    // ---- the tool calls that completed ----

    private static string ToolUse(string id) => $$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"{{{id}}}","name":"Read","input":{}}]}}""";

    private static string ToolResult(string id, bool isError) =>
        $$$"""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{id}}}"{{{(isError ? ",\"is_error\":true" : "")}}},"content":"c"}]}}""";

    private static readonly string failed = Result(""" "is_error":true,"api_error_status":402,"result":"credits" """.Trim());

    [Fact]
    public void ACompletedToolCallCounts() =>
        Assert.Equal(1, backend.Parse(Stream(Init, ToolUse("t1"), ToolResult("t1", isError: false), failed), "", 1).ToolCallsCompleted);

    [Fact]
    public void AToolCallThatCameBackAsAnErrorDidNothing() =>
        Assert.Equal(0, backend.Parse(Stream(Init, ToolUse("t1"), ToolResult("t1", isError: true), failed), "", 1).ToolCallsCompleted);

    [Fact]
    public void AToolCallWithNoResultYetCountsBecauseItMayHaveRun() =>
        Assert.Equal(1, backend.Parse(Stream(Init, ToolUse("t1"), failed), "", 1).ToolCallsCompleted);

    [Fact]
    public void AStreamWithNoToolCallsCountsZero() =>
        Assert.Equal(0, backend.Parse(Stream(Init, failed), "", 1).ToolCallsCompleted);

    // Several `result` events are a live session's turns: a human spoke, so the count is not trusted.
    [Fact]
    public void SeveralResultEventsMeanALiveSessionAndNoCount() =>
        Assert.Null(backend.Parse(Stream(Init, Result(""" "is_error":false,"result":"turn one" """.Trim()), failed), "", 1).ToolCallsCompleted);

    [Fact]
    public void AStreamWithNoResultAtAllIsAFailureWithNoKindAndNoCount()
    {
        ParsedOutput parsed = backend.Parse(Stream(Init, ToolUse("t1")), stderr: "killed", exitCode: 137);

        Assert.True(parsed.IsError);
        Assert.Null(parsed.ErrorKind);
        Assert.Null(parsed.ToolCallsCompleted);
    }

    // #127: a replayed user line (an echo of a message taken, or a resumed session's history) is not an event of the run, so an errored tool_result
    // in it cannot "forget" a call that really did run.
    [Fact]
    public void AReplayedErroredToolResultDoesNotForgetAToolCall()
    {
        string replayed = /*lang=json,strict*/ """{"type":"user","isReplay":true,"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"x"}]}}""";

        Assert.Equal(1, backend.Parse(Stream(Init, ToolUse("t1"), replayed, failed), "", 1).ToolCallsCompleted);
    }

    [Fact]
    public void ALiveRunsReplayedMessageIsNotATurnAndASingleResultStaysSingle()
    {
        string echo = /*lang=json,strict*/ """{"type":"user","uuid":"u-1","isReplay":true,"message":{"role":"user","content":[{"type":"text","text":"hi"}]}}""";

        ParsedOutput parsed = backend.Parse(Stream(Init, echo, failed), "", 1);

        Assert.Equal(0, parsed.ToolCallsCompleted);
        Assert.Equal(1, parsed.Steps);
    }
}
