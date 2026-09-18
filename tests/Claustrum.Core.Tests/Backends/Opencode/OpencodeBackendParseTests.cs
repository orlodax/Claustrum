using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;

namespace Claustrum.Core.Tests.Backends.Opencode;

// error.jsonl is a REAL capture (2026-09-18, `opencode run` against a real installed opencode-ai
// 1.18.31 with no working provider credential — see NOTES.md "The opencode backend"). success.jsonl
// is fabricated: no working credential was available to record a genuine success, so it is built
// from opencode's public SDK part-type shapes and the event-name strings confirmed live in the
// installed binary, same disclosure as tests/fixtures/api/'s fixtures.
public sealed class OpencodeBackendParseTests
{
    private readonly OpencodeBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", name);

    [Fact]
    public void SuccessJsonlUsesTheLastUpdateOfEachTextPartInOrder()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("No defects found. The change looks correct.\n\n```claustrum-report\n{\"status\":\"done\",\"findings\":[],\"would_change_if_broader\":[]}\n```", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Equal("ses_fabricated00001", parsed.SessionId);
        Assert.Equal(0.00094m, parsed.CostUsd);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(1180, parsed.Usage!.InputTokens);
        Assert.Equal(88, parsed.Usage.OutputTokens);
        Assert.Empty(parsed.ReportedEdits);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void RealErrorCaptureExtractsTheMessageAndSessionId()
    {
        string stdout = File.ReadAllText(FixturePath("error.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("Unexpected server error. Check server logs for details.", parsed.FinalMessage);
        Assert.Equal("ses_f4bc88058ffeoEALuwpU2XsjoV", parsed.SessionId);
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
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"hi"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("hi", parsed.FinalMessage);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void ARepeatedUpdateToTheSamePartReplacesRatherThanAppendsItsText()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"partial"}}""",
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"final text"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("final text", parsed.FinalMessage);
    }

    [Fact]
    public void MultipleTextPartsAreConcatenatedInFirstSeenOrder()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p2","type":"text","text":"second"}}""",
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p1","type":"text","text":"first-late-arrival"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        // p2 was seen first in the stream, so its text comes first regardless of id ordering.
        Assert.Equal("secondfirst-late-arrival", parsed.FinalMessage);
    }

    [Fact]
    public void NonTextNonStepFinishPartsAreIgnored()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p1","type":"tool","text":"ignored"}}""",
            /*lang=json,strict*/ """{"type":"message.part.updated","sessionID":"ses_1","part":{"id":"p2","type":"text","text":"kept"}}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("kept", parsed.FinalMessage);
    }
}
