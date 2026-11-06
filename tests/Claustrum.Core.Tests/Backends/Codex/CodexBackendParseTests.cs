using System.Text.Json.Nodes;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Codex;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Codex;

// Fixtures under tests/fixtures/codex/ are real `codex-cli 0.166.0` captures from 2026-11-04, paths scrubbed to /work (NOTES.md
// "The codex backend, validated against a real install").
public sealed class CodexBackendParseTests
{
    private readonly CodexBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", name));

    [Fact]
    public void SuccessReadsTheMessageTheThreadIdAndTheFourUsageNumbers()
    {
        ParsedOutput parsed = backend.Parse(Fixture("success.jsonl"), stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("PONG", parsed.FinalMessage);
        Assert.Equal("01a1a6bc-b0cb-77f2-9473-70a50656850e", parsed.SessionId);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(new Usage(13266, 6, 0, 13263), parsed.Usage);
        Assert.Equal(0, parsed.ToolCallsCompleted);
        Assert.Null(parsed.ErrorKind);
    }

    [Fact]
    public void TheFinalMessageIsTheLastAgentMessageNotThePreamble()
    {
        ParsedOutput parsed = backend.Parse(Fixture("tools-read-only.jsonl"), stderr: "", exitCode: 0);

        Assert.StartsWith("`a.txt` contains:", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(1, parsed.ToolCallsCompleted);
    }

    [Theory]
    [InlineData("file-change.jsonl", 1)]
    [InlineData("command-failed.jsonl", 1)]
    [InlineData("sandbox-denied-write.jsonl", 1)]
    public void EveryCompletedToolItemCountsAndARefusedCommandNeverShowsUp(string fixture, int expected)
    {
        ParsedOutput parsed = backend.Parse(Fixture(fixture), stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal(expected, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void AResumedRunKeepsTheThreadId()
    {
        ParsedOutput parsed = backend.Parse(Fixture("resume.jsonl"), stderr: "", exitCode: 0);

        Assert.Equal("01a1a6bc-f0fd-7042-896e-cecd203c68f8", parsed.SessionId);
        Assert.Equal("I read `a.txt`, which contained `hello from fixture`.", parsed.FinalMessage);
    }

    [Fact]
    public void A401IsProviderAuthAndTheRetryNoticesAreNotTheOutcome()
    {
        ParsedOutput parsed = backend.Parse(Fixture("auth-401.jsonl"), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ProviderAuth, parsed.ErrorKind);
        Assert.StartsWith("unexpected status 401 Unauthorized", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal(parsed.FinalMessage, parsed.ErrorDetail);
        Assert.Equal(0, parsed.ToolCallsCompleted);
    }

    [Fact]
    public void AnUnknownModelIsModelNotFound()
    {
        ParsedOutput parsed = backend.Parse(Fixture("model-not-found.jsonl"), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.ModelNotFound, parsed.ErrorKind);
        Assert.Equal("01a1a6bd-0e5a-73e1-b5c6-ea8cdaf36016", parsed.SessionId);
    }

    [Fact]
    public void AJsonBodyIsClassifiedByItsStatusAndTypeAnd400InvalidRequestIsOther()
    {
        ParsedOutput parsed = backend.Parse(Fixture("invalid-request-400.jsonl"), stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(ErrorKind.Other, parsed.ErrorKind);
        Assert.Contains("invalid_enum_value", parsed.ErrorDetail, StringComparison.Ordinal);
    }

    // Codex 0.166's own sentences for a 429, a 5xx and a full model (protocol/src/error.rs), never `unexpected status 429`.
    [Theory]
    [InlineData("exceeded retry limit, last status: 429 Too Many Requests, request id: req_1", ErrorKind.ProviderQuota)]
    [InlineData("Quota exceeded. Check your plan and billing details.", ErrorKind.ProviderQuota)]
    [InlineData("You\u2019ve hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/settings/usage to purchase more credits", ErrorKind.ProviderQuota)]
    [InlineData("Your workspace is out of credits. Add credits to continue.", ErrorKind.ProviderQuota)]
    [InlineData("rate limit exceeded: try again in 20s", ErrorKind.ProviderQuota)]
    [InlineData("Selected model is at capacity. Please try a different model.", ErrorKind.ProviderTransport)]
    [InlineData("We\u2019re currently experiencing high demand, which may cause temporary errors.", ErrorKind.ProviderTransport)]
    [InlineData("exceeded retry limit, last status: 503 Service Unavailable", ErrorKind.ProviderTransport)]
    [InlineData(/*lang=json,strict*/ """{"error":{"type":"insufficient_quota"},"status":429}""", ErrorKind.ProviderQuota)]
    [InlineData("the model said: The model `x` does not exist or you do not have access to it.", ErrorKind.Other)]
    [InlineData("I think Quota exceeded. Check your plan and billing details.", ErrorKind.Other)]
    [InlineData("unexpected status", ErrorKind.Other)]
    public void ARefusalIsPlacedOnlyByItsStatusItsTypeOrCodexsOwnSentence(string message, ErrorKind expected)
    {
        string line = new JsonObject { ["type"] = "turn.failed", ["error"] = new JsonObject { ["message"] = message } }.ToJsonString();
        ParsedOutput parsed = backend.Parse(line, stderr: "", exitCode: 1);

        Assert.Equal(expected, parsed.ErrorKind);
    }

    [Fact]
    public void OutsideAGitRepoTheStderrLineIsTheMessage()
    {
        ParsedOutput parsed = backend.Parse("", Fixture("not-a-git-repo.stderr.txt"), exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("Not inside a trusted directory and --skip-git-repo-check was not specified.", parsed.FinalMessage);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void NonJsonAndUnknownLinesAreSkippedAndANonZeroExitStillFails()
    {
        string stdout = "banner\n[1,2]\n{\"type\":\"thread.started\",\"thread_id\":\"t\"}\n{\"type\":\"item.completed\",\"item\":\"x\"}\n"
            + /*lang=json,strict*/ "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":\"many\"}}\n";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 2);

        Assert.True(parsed.IsError);
        Assert.Equal("t", parsed.SessionId);
        Assert.Equal("", parsed.FinalMessage);
        Assert.Equal(new Usage(null, null, null, null), parsed.Usage);
    }
}
