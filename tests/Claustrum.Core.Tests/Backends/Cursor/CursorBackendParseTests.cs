using System.Text.Json.Nodes;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Cursor;

namespace Claustrum.Core.Tests.Backends.Cursor;

// Fixtures under tests/fixtures/cursor/ are real captures from a live `cursor-agent 2026.09.18-9a7762b`
// run, not fabricated (NOTES.md "The cursor backend, validated against a real install", issues
// #13/#14) — `usage`'s camelCase keys and the stderr-only failure shape were both measured here,
// not guessed. `error.json` no longer exists: both real failure modes captured print human text on
// stderr with empty stdout, never a JSON error document.
public sealed class CursorBackendParseTests
{
    private readonly CursorBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "cursor", name);

    [Fact]
    public void SuccessJsonExtractsTheFinalMessageSessionAndAllFourUsageNumbers()
    {
        string stdout = File.ReadAllText(FixturePath("success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("Created `hello.txt` with the contents `hi`.", parsed.FinalMessage);
        Assert.False(parsed.IsError);
        Assert.Equal("79f194af-4fcb-4d3b-8201-6c9caa501e27", parsed.SessionId);
        Assert.Null(parsed.CostUsd);
        Assert.Equal(9277, parsed.Usage!.InputTokens);
        Assert.Equal(110, parsed.Usage.OutputTokens);
        Assert.Equal(26240, parsed.Usage.CacheReadInputTokens);
        Assert.Equal(0, parsed.Usage.CacheCreationInputTokens);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void PlanModeJsonParsesAsANormalSuccessfulResult()
    {
        string stdout = File.ReadAllText(FixturePath("plan-mode.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
        Assert.StartsWith("Plan mode blocks both of those actions.", parsed.FinalMessage, StringComparison.Ordinal);
        Assert.Equal("a0fb7ccb-a961-4b18-a838-51175e1b022f", parsed.SessionId);
    }

    // Measured shape for both real failure modes reachable on a Free plan (workspace trust, named-model
    // refusal): empty stdout, the human error line on stderr, exit 1 — never a JSON error document.
    [Fact]
    public void EmptyStdoutWithStderrAndExitOneUsesStderrAsTheFinalMessage()
    {
        string stderr = File.ReadAllText(FixturePath("named-model-refused-stderr.txt"));

        ParsedOutput parsed = backend.Parse(stdout: "", stderr, exitCode: 1);

        Assert.Equal(stderr, parsed.FinalMessage);
        Assert.True(parsed.IsError);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void NonJsonStdoutFallsBackToRawText()
    {
        const string stdout = "just plain text, not json";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal(stdout, parsed.FinalMessage);
        Assert.Null(parsed.Raw);
        Assert.False(parsed.IsError);
    }

    [Fact]
    public void NonzeroExitIsAlwaysAnErrorRegardlessOfIsErrorField()
    {
        string stdout = File.ReadAllText(FixturePath("success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.True(parsed.IsError);
    }

    // No real capture of `is_error: true` exists (NOTES.md: nothing on the Free plan tested here got
    // far enough into a session to produce one) — synthesized inline from the same field shape
    // success.json carries, to prove Parse reads is_error at all, not that this exact document is real.
    [Fact]
    public void IsErrorTrueInTheDocumentIsAnErrorEvenWithExitCodeZero()
    {
        JsonObject document = new()
        {
            ["type"] = "result",
            ["subtype"] = "error",
            ["is_error"] = true,
            ["result"] = "the model declined",
            ["session_id"] = "synthetic-session",
            ["usage"] = new JsonObject { ["inputTokens"] = 1, ["outputTokens"] = 1, ["cacheReadTokens"] = 0, ["cacheWriteTokens"] = 0 },
        };

        ParsedOutput parsed = backend.Parse(document.ToJsonString(), stderr: "", exitCode: 0);

        Assert.True(parsed.IsError);
        Assert.Equal("the model declined", parsed.FinalMessage);
    }
}
