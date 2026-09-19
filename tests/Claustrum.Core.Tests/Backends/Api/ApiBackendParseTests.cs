using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;

namespace Claustrum.Core.Tests.Backends.Api;

// Fixtures live in tests/fixtures/api/. Unlike tests/fixtures/claude/ (recorded from the real CLI),
// these are hand-built from OpenRouter's and Anthropic's published API response shapes — this
// project has no API key to record a live call against, and docs/PLAN.md flags the api backend as
// best-effort/UNCONFIRMED until validated against a real account (same status as opencode/cursor/
// copilot's own fixtures).
public sealed class ApiBackendParseTests
{
    private readonly ApiBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "api", name);

    [Fact]
    public void OpenRouterSuccessExtractsMessageUsageAndCost()
    {
        string stdout = File.ReadAllText(FixturePath("openrouter-success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Contains("No defects found", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Null(parsed.SessionId);
        Assert.Equal(0.00182m, parsed.CostUsd);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(1240, parsed.Usage!.InputTokens);
        Assert.Equal(96, parsed.Usage.OutputTokens);
        Assert.Empty(parsed.ReportedEdits);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void AnthropicSuccessExtractsTextAndUsageWithNoCost()
    {
        string stdout = File.ReadAllText(FixturePath("anthropic-success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Contains("No defects found", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(1240, parsed.Usage!.InputTokens);
        Assert.Equal(96, parsed.Usage.OutputTokens);
    }

    [Fact]
    public void OpenRouterErrorShapeIsErrorTrueWithMessageExtracted()
    {
        string stdout = File.ReadAllText(FixturePath("error.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.FinalMessage);
    }

    [Fact]
    public void AnthropicErrorShapeIsErrorTrueWithMessageExtracted()
    {
        string stdout = File.ReadAllText(FixturePath("anthropic-error.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.FinalMessage);
    }

    [Fact]
    public void EmptyStdoutFallsBackToStderrAsFinalMessage()
    {
        ParsedOutput parsed = backend.Parse(stdout: "   ", stderr: "boom", exitCode: 1);

        Assert.Equal("boom", parsed.FinalMessage);
        Assert.True(parsed.IsError);
    }

    [Fact]
    public void MalformedJsonWithZeroExitIsNotForcedToError()
    {
        ParsedOutput parsed = backend.Parse("not json at all", stderr: "", exitCode: 0);

        Assert.Equal("not json at all", parsed.FinalMessage);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void MalformedJsonWithNonzeroExitIsAnError()
    {
        ParsedOutput parsed = backend.Parse("not json at all", stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
    }
}
