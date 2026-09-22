using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;

namespace Claustrum.Core.Tests.Backends.Api;

// Fixtures live in tests/fixtures/api/. openrouter-success.json, error.json, error-invalid-model.json
// and anthropic-error.json are genuine captures against live endpoints (2026-09-22, NOTES.md "The
// opencode and api backends, validated against real endpoints", item 6) — anthropic-success.json is
// the one fixture here still fabricated, for lack of an ANTHROPIC_API_KEY on the capturing machine.
public sealed class ApiBackendParseTests
{
    private readonly ApiBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "api", name);

    [Fact]
    public void OpenRouterSuccessExtractsMessageUsageAndCost()
    {
        string stdout = File.ReadAllText(FixturePath("openrouter-success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Contains("OK", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("claustrum-report", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.False(parsed.IsError);
        Assert.Null(parsed.SessionId);
        Assert.Equal(0.000040318m, parsed.CostUsd);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(1883, parsed.Usage!.InputTokens);
        Assert.Equal(25, parsed.Usage.OutputTokens);
        Assert.Equal(1827, parsed.Usage.CacheReadInputTokens);
        Assert.Equal(0, parsed.Usage.CacheCreationInputTokens);
        Assert.Empty(parsed.ReportedEdits);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void AnthropicSuccessExtractsTextAndUsageWithNoCost()
    {
        string stdout = File.ReadAllText(FixturePath("anthropic-success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Contains("No defects found", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.False(parsed.IsError);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(1240, parsed.Usage!.InputTokens);
        Assert.Equal(96, parsed.Usage.OutputTokens);
    }

    [Fact]
    public void OpenRouterAuthErrorShapeIsErrorTrueWithMessageExtracted()
    {
        string stdout = File.ReadAllText(FixturePath("error.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Equal("User not found.", parsed.FinalMessage);
    }

    [Fact]
    public void OpenRouterInvalidModelErrorShapeIsErrorTrueWithMessageExtracted()
    {
        string stdout = File.ReadAllText(FixturePath("error-invalid-model.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnthropicErrorShapeIsErrorTrueWithMessageExtracted()
    {
        string stdout = File.ReadAllText(FixturePath("anthropic-error.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 22);

        Assert.True(parsed.IsError);
        Assert.Equal("API key is invalid.", parsed.FinalMessage);
    }

    // A billed, successful response can omit usage with a JSON **null** rather than dropping the
    // property; TryGetProperty on a null element throws, so before the ValueKind guard this turned a
    // paid success into a Failed run out of Parse (NOTES.md item 6).
    [Fact]
    public void OpenRouterSuccessWithNullUsageDoesNotThrowAndReportsNoUsage()
    {
        string stdout = /*lang=json,strict*/ """{"choices":[{"message":{"content":"ok"}}],"usage":null}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("ok", parsed.FinalMessage);
        Assert.Null(parsed.Usage);
        Assert.Null(parsed.CostUsd);
    }

    [Fact]
    public void AnthropicSuccessWithNullUsageDoesNotThrowAndReportsNoUsage()
    {
        string stdout = /*lang=json,strict*/ """{"content":[{"type":"text","text":"ok"}],"usage":null}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("ok", parsed.FinalMessage);
        Assert.Null(parsed.Usage);
    }

    // OpenRouter emits a JSON null for an optional field it has nothing to say about (`refusal`,
    // `service_tier` in the genuine success capture) — an `error` property present but null is not a
    // failure, the same ValueKind discipline the null-usage guard above already applies one level up.
    [Fact]
    public void OpenRouterSuccessWithANullErrorPropertyIsStillSuccess()
    {
        string stdout = /*lang=json,strict*/
            """{"error":null,"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":2,"cost":0.0001}}""";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.Equal("ok", parsed.FinalMessage);
        Assert.Equal(0.0001m, parsed.CostUsd);
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
