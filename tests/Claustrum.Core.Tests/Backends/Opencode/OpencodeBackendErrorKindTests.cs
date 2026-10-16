using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #126: error_kind and the completed-tool-call count from opencode's stream. quota-402.jsonl is the real stdout.log of job
// 20261015-164125-2f5f1b1d (2026-10-15), trimmed by hand: the prompt shortened, the raw input echo and the paths dropped, every event and every key
// the parser reads kept.
public sealed class OpencodeBackendErrorKindTests
{
    private readonly OpencodeBackend backend = new(platform: null!);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", name));

    private static string Error(string errorJson) => $$"""{"type":"error","sessionID":"ses_1","error":{{errorJson}}}""";

    [Fact]
    public void TheReal402IsProviderQuotaWithTheProvidersWordsAndNoCompletedToolCall()
    {
        ParsedOutput parsed = backend.Parse(Fixture("quota-402.jsonl"), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ProviderQuota, parsed.ErrorKind);
        Assert.StartsWith("This request would exceed your available credits", parsed.ErrorDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("I'll start by understanding", parsed.ErrorDetail, StringComparison.Ordinal);

        // The text the first step wrote stays in the message; the errored `subagent` call did nothing, so zero tool calls completed.
        Assert.Contains("I'll start by understanding", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("add credits", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void TheRealAuthCaptureIsProviderAuthWithNoToolCalls()
    {
        ParsedOutput parsed = backend.Parse(Fixture("error.jsonl"), stderr: "", exitCode: 1);

        Assert.Equal(ErrorKind.ProviderAuth, parsed.ErrorKind);
        Assert.Equal("User not found.", parsed.ErrorDetail);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void TheV1ErrorShapeCarriesNeitherTypeNorStatusAndIsOther()
    {
        ParsedOutput parsed = backend.Parse(Fixture("error-v1.jsonl"), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.no-route","message":"Variant unavailable"}""", ErrorKind.ModelNotFound)]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.auth","message":"x","status":401}""", ErrorKind.ProviderAuth)]
    [InlineData(/*lang=json,strict*/ """{"type":"unknown","message":"Agent not found"}""", ErrorKind.Other)]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.rate","message":"slow down","status":429}""", ErrorKind.ProviderQuota)]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.x","message":"bad gateway","status":502}""", ErrorKind.ProviderTransport)]
    [InlineData(/*lang=json,strict*/ """{"message":"no type, no status"}""", ErrorKind.Other)]
    public void AnErrorEventsStatusThenTypeDecideItsKind(string errorJson, ErrorKind expected) =>
        Assert.Equal(expected, backend.Parse(Error(errorJson), stderr: "", exitCode: 1).ErrorKind);

    [Fact]
    public void ASuccessfulRunHasNoErrorKindAndCountsItsCompletedToolCalls()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_1","part":{"id":"c1","type":"tool","tool":"read","state":{"status":"completed"}}}""",
            /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_1","part":{"id":"c2","type":"tool","tool":"bash","state":{"status":"error","error":"denied"}}}""",
            /*lang=json,strict*/ """{"type":"text","sessionID":"ses_1","part":{"id":"t1","type":"text","text":"done"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Null(parsed.ErrorKind);
        Assert.Equal(1, parsed.ToolCallsCompleted);
    }

    // A call caught pending or running when the stream stopped may have done something: it counts, so the doubt never retries a run that worked.
    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("completed")]
    public void ACallThatDidNotErrorCountsAsWorkWhateverItsState(string status)
    {
        string stdout = string.Join('\n',
        [
            $$$$"""{"type":"tool_use","sessionID":"ses_1","part":{"id":"c1","type":"tool","tool":"edit","state":{"status":"{{{{status}}}}"}}}""",
            Error(/*lang=json,strict*/ """{"type":"provider.quota","message":"credits","status":402}"""),
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.Equal(ErrorKind.ProviderQuota, parsed.ErrorKind);
        Assert.Equal(1, parsed.ToolCallsCompleted);
    }

    // The last event for a call is the one that counts: a call that ran and then errored did nothing.
    [Fact]
    public void ACallsLastEventDecidesWhetherItCounts()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_1","part":{"id":"c1","type":"tool","tool":"read","state":{"status":"running"}}}""",
            /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_1","part":{"id":"c1","type":"tool","tool":"read","state":{"status":"error","error":"gone"}}}""",
            Error(/*lang=json,strict*/ """{"type":"provider.quota","message":"credits","status":402}"""),
        ]);

        Assert.Equal(0, backend.Parse(stdout, stderr: "", exitCode: 1).ToolCallsCompleted);
    }

    [Fact]
    public void AFailureWithNoErrorEventHasNoKind()
    {
        ParsedOutput parsed = backend.Parse("garbage", stderr: "boom", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Null(parsed.ErrorKind);
    }
}
