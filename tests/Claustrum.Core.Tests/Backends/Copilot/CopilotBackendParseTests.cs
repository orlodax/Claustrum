using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Copilot;

namespace Claustrum.Core.Tests.Backends.Copilot;

// auth-failure-stderr.txt, success.jsonl, effort-refused-stdout.jsonl and effort-refused-stderr.txt
// are all REAL captures against an authenticated GitHub Copilot CLI 1.0.87 (2026-09-22, issue #13 —
// NOTES.md "The copilot backend, validated against a real install" holds the box-by-box evidence and
// the redaction list for success.jsonl). Every event shape here is `{"type":…,"data":{…}}`; the CLI's
// own closing line, `{"type":"result","sessionId":…,"exitCode":…}`, is the only exception.
public sealed class CopilotBackendParseTests
{
    private readonly CopilotBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "copilot", name);

    [Fact]
    public void RealAuthFailureFallsBackToStderrWithEmptyStdout()
    {
        string stderr = File.ReadAllText(FixturePath("auth-failure-stderr.txt"));

        ParsedOutput parsed = backend.Parse(stdout: "", stderr, exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(stderr, parsed.FinalMessage);
        Assert.Null(parsed.SessionId);
        Assert.Null(parsed.Raw);
    }

    // A mid-session refusal is the opposite shape from a startup failure: the CLI routes
    // `session.error` to stdout as an event and writes nothing to stderr, so stderr must not win here
    // the way it does for the plumbing-only case below.
    [Fact]
    public void EffortRefusalPrefersStderrOverPlumbingOnlyStdout()
    {
        string stdout = File.ReadAllText(FixturePath("effort-refused-stdout.jsonl"));
        string stderr = File.ReadAllText(FixturePath("effort-refused-stderr.txt"));

        ParsedOutput parsed = backend.Parse(stdout, stderr, exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal(stderr, parsed.FinalMessage);
    }

    [Fact]
    public void SuccessFixtureIsFortyTwoLinesThatAllParseAsJson()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));
        string[] lines = stdout.Trim().Split('\n');

        Assert.Equal(42, lines.Length);
        Assert.All(lines, line => JsonDocument.Parse(line).Dispose());
    }

    [Fact]
    public void SuccessJsonlUsesTheLastNonEmptyAssistantMessageContent()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("4226b2e4-4a96-45e2-bdd8-35c8b5f63e07", parsed.SessionId);
        Assert.StartsWith("Created `hello.txt` containing `hi`.", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("```claustrum-report", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.EndsWith("```", parsed.FinalMessage, StringComparison.Ordinal);
        // Usage/CostUsd are null by measurement, not omission: the CLI suppresses `assistant.usage`
        // from the stream and the closing `result` reports only `premiumRequests` and durations.
        Assert.Null(parsed.Usage);
        Assert.Null(parsed.CostUsd);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void EmptyStdoutWithZeroExitIsNotAnError()
    {
        ParsedOutput parsed = backend.Parse(stdout: "", stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
    }

    [Fact]
    public void NonzeroExitWithNonEmptyStdoutIsStillAnError()
    {
        ParsedOutput parsed = backend.Parse(/*lang=json,strict*/ """{"type":"assistant.message","data":{"content":"partial"}}""", stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("partial", parsed.FinalMessage);
    }

    [Fact]
    public void MalformedLinesAreSkippedNotFatal()
    {
        string stdout = string.Join('\n',
        [
            "not json",
            /*lang=json,strict*/ """{"type":"assistant.message","data":{"content":"hi"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("hi", parsed.FinalMessage);
    }

    [Fact]
    public void WhenNoLineHasRecognizableTextTheRawStreamIsTheFallback()
    {
        string stdout = /*lang=json,strict*/ """{"type":"something_unrecognized","value":42}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(stdout, parsed.FinalMessage);
    }

    [Fact]
    public void LastNonEmptyAssistantMessageContentWinsOverEarlierEmptyOnes()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"assistant.message","data":{"content":""}}""",
            /*lang=json,strict*/ """{"type":"assistant.message","data":{"content":"first"}}""",
            /*lang=json,strict*/ """{"type":"assistant.message","data":{"content":""}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("first", parsed.FinalMessage);
    }

    // NOTES.md "Sub-agent tagging is unobserved, so Parse does not special-case it": `agentId` was
    // never observed on a real `assistant.message` event, and the filter that used to skip such
    // events on its theoretical presence removed nothing and could only mis-fire. It is gone.
    [Fact]
    public void AssistantMessageWithAgentIdIsStillUsedNoSubAgentFilter()
    {
        string stdout = /*lang=json,strict*/ """{"type":"assistant.message","agentId":"some-agent","data":{"content":"hi from agent"}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("hi from agent", parsed.FinalMessage);
    }

    [Fact]
    public void SessionErrorEventIsReadAsTheFinalMessage()
    {
        string stdout = /*lang=json,strict*/ """{"type":"session.error","data":{"message":"boom"}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("boom", parsed.FinalMessage);
    }

    [Fact]
    public void SessionStartEventProvidesSessionIdWhenPresent()
    {
        string stdout = /*lang=json,strict*/ """{"type":"session.start","data":{"sessionId":"abc-123"}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("abc-123", parsed.SessionId);
    }

    // The real stream carries no `session.start` at all: the session id arrives only on the CLI's
    // own closing `result` line, which is not a session event (no `data` nesting).
    [Fact]
    public void ResultLineSessionIdOverridesAnEarlierSessionStart()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"session.start","data":{"sessionId":"old"}}""",
            /*lang=json,strict*/ """{"type":"result","sessionId":"new","exitCode":0}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("new", parsed.SessionId);
    }
}
