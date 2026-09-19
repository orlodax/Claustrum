using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Cursor;

namespace Claustrum.Core.Tests.Backends.Cursor;

// Entirely fabricated, unlike claude's (recorded) or opencode's/copilot's (partially confirmed
// live) fixtures — Cursor's CLI could not be installed or probed in this environment at all (see
// NOTES.md "The cursor backend"). Modeled directly on docs/PLAN.md §A3's own best-effort field
// names ("result", "session_id", "usage"); flagged unconfirmed until a teammate with the real CLI
// validates it, per the plan's own stated status for this backend.
public sealed class CursorBackendParseTests
{
    private readonly CursorBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "cursor", name);

    [Fact]
    public void SuccessJsonExtractsResultSessionAndUsage()
    {
        string stdout = File.ReadAllText(FixturePath("success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Contains("No defects found", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Equal("fabricated-cursor-session-0001", parsed.SessionId);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(1150, parsed.Usage!.InputTokens);
        Assert.Equal(84, parsed.Usage.OutputTokens);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void ErrorJsonIsErrorTrueEvenWithZeroExitCode()
    {
        string stdout = File.ReadAllText(FixturePath("error.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.True(parsed.IsError);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.FinalMessage);
        Assert.Equal("fabricated-cursor-session-0002", parsed.SessionId);
    }

    [Fact]
    public void NonzeroExitIsAlwaysAnErrorRegardlessOfIsErrorField()
    {
        string stdout = File.ReadAllText(FixturePath("success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
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
    public void MalformedJsonFallsBackToRawTextPerThePlansOwnElseTextRule()
    {
        string stdout = "just plain text, not json";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(stdout, parsed.FinalMessage);
        Assert.Null(parsed.Raw);
        Assert.False(parsed.IsError);
    }
}
