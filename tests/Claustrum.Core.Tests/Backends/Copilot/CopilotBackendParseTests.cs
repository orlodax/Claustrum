using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Copilot;

namespace Claustrum.Core.Tests.Backends.Copilot;

// auth-failure-stderr.txt is a REAL capture (2026-09-18, `copilot -p ... --output-format json`
// against a real installed @github/copilot 1.0.86 with no reachable GitHub Copilot subscription —
// see NOTES.md "The copilot backend"): stdout was empty, this text came out on stderr, exit code 1.
// success.jsonl is fabricated — no authenticated session was reachable to record a genuine one — and
// the stripped copilot binary offered no event-name strings the way opencode's did, so this fixture
// and the key names Parse looks for are a best-effort guess, flagged the same as the other M3
// backends' unconfirmed fixtures.
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

    [Fact]
    public void SuccessJsonlUsesTheLastAssistantMessageContent()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("No defects found. The change looks correct.\n\n```claustrum-report\n{\"status\":\"done\",\"findings\":[],\"would_change_if_broader\":[]}\n```", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890", parsed.SessionId);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(1300, parsed.Usage!.InputTokens);
        Assert.Equal(110, parsed.Usage.OutputTokens);
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
        ParsedOutput parsed = backend.Parse(/*lang=json,strict*/ """{"type":"assistant_message","content":"partial"}""", stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("partial", parsed.FinalMessage);
    }

    [Fact]
    public void MalformedLinesAreSkippedNotFatal()
    {
        string stdout = string.Join('\n',
        [
            "not json",
            /*lang=json,strict*/ """{"type":"assistant_message","content":"hi"}""",
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
    public void PromptTokensNamingIsAlsoAccepted()
    {
        string stdout = /*lang=json,strict*/ """{"type":"usage","content":"done","usage":{"prompt_tokens":50,"completion_tokens":9}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(50, parsed.Usage!.InputTokens);
        Assert.Equal(9, parsed.Usage.OutputTokens);
    }
}
