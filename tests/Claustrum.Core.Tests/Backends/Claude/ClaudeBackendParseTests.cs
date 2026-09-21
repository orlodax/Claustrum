using System.Text.Json.Nodes;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Claude;

// Fixtures live in tests/fixtures/claude/ (recorded 2026-09-13, see error.txt's own header) and are
// copied to the test output directory by Claustrum.Core.Tests.csproj.
public sealed class ClaudeBackendParseTests
{
    private readonly ClaudeBackend backend = new(platform: null!); // Parse never touches IPlatform.

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", name);

    [Fact]
    public void SuccessJsonExtractsResultSessionCostAndUsage()
    {
        string stdout = File.ReadAllText(FixturePath("success.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("Hey! 👋 What can I help you with today?", parsed.FinalMessage);
        Assert.Equal("5d6fe1fe-3573-497c-a0cf-4e193f16da69", parsed.SessionId);
        Assert.Equal(0.0384621m, parsed.CostUsd);
        Assert.False(parsed.IsError);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(10, parsed.Usage!.InputTokens);
        Assert.Equal(83, parsed.Usage.OutputTokens);
        Assert.Equal(30811, parsed.Usage.CacheReadInputTokens);
        Assert.Equal(17478, parsed.Usage.CacheCreationInputTokens);
        Assert.Empty(parsed.ReportedEdits);
        Assert.NotNull(parsed.Raw);
    }

    [Fact]
    public void SuccessJsonlStreamUsesTheLastResultLine()
    {
        string stdout = File.ReadAllText(FixturePath("success.jsonl"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("pong", parsed.FinalMessage);
        Assert.Equal("769cd518-1e5e-4e42-ab2b-7fb32a63536a", parsed.SessionId);
        Assert.Equal(0.030420700000000002m, parsed.CostUsd);
        Assert.False(parsed.IsError);
        Assert.Equal(10, parsed.Usage!.InputTokens);
        Assert.Equal(54, parsed.Usage.OutputTokens);
    }

    [Fact]
    public void ErrorFixtureIsErrorTrueWithNonzeroExit()
    {
        (string stdout, string stderr) = ReadErrorFixture();

        ParsedOutput parsed = backend.Parse(stdout, stderr, exitCode: 1);

        Assert.True(parsed.IsError);
        Assert.Equal("8b5f8718-50a9-4e53-b4dd-f797dc532050", parsed.SessionId);
        Assert.Contains("this-model-does-not-exist-xyz", parsed.FinalMessage);
        Assert.Equal(0m, parsed.CostUsd);
    }

    [Fact]
    public void EmptyStdoutFallsBackToStderrAsFinalMessage()
    {
        ParsedOutput parsed = backend.Parse(stdout: "   ", stderr: "boom", exitCode: 1);

        Assert.Equal("boom", parsed.FinalMessage);
        Assert.True(parsed.IsError);
        Assert.Null(parsed.SessionId);
        Assert.Empty(parsed.ReportedEdits);
    }

    [Fact]
    public void EmptyStdoutWithZeroExitIsNotAnError()
    {
        ParsedOutput parsed = backend.Parse(stdout: "", stderr: "", exitCode: 0);

        Assert.False(parsed.IsError);
    }

    [Fact]
    public void MalformedJsonWithNoParseableLineFallsBackToRawText()
    {
        string stdout = "{not json, and no closing brace";

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 1);

        Assert.Equal(stdout, parsed.FinalMessage);
        Assert.Null(parsed.SessionId);
        Assert.Null(parsed.Raw);
        Assert.True(parsed.IsError);
    }

    [Fact]
    public void StreamJsonToolUseEditAndWriteBecomeReportedEdits()
    {
        string stdout = string.Join('\n',
        [
            /*lang=json,strict*/
                                 """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/foo.cs"}}]}}""",
            /*lang=json,strict*/
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"src/bar.cs"}}]}}""",
            /*lang=json,strict*/
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}""",
            /*lang=json,strict*/
            """{"type":"result","result":"done","session_id":"s1","total_cost_usd":0.01,"is_error":false}""",
        ]);

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("done", parsed.FinalMessage);
        Assert.Equal(2, parsed.ReportedEdits.Length);
        Assert.Contains(parsed.ReportedEdits, e => e.Path == "src/foo.cs" && e.Kind == ChangeKind.Modified);
        Assert.Contains(parsed.ReportedEdits, e => e.Path == "src/bar.cs" && e.Kind == ChangeKind.Added);
    }

    // A budget-terminated document (2026-09-21, issue #12) carries no `result` at all: the message
    // sits in `errors`, and `subtype` names the failure class. Recorded as a genuine capture, not
    // synthesized (tests/fixtures/claude/error-max-budget.json's own generation is the real thing).
    [Fact]
    public void MaxBudgetFixtureFallsBackToTheJoinedErrorsEntry()
    {
        string stdout = File.ReadAllText(FixturePath("error-max-budget.json"));

        ParsedOutput parsed = backend.Parse(stdout, stderr: "", exitCode: 0);

        Assert.Equal("Reached maximum budget ($0.05)", parsed.FinalMessage);
        Assert.True(parsed.IsError);
        Assert.Equal(0.1200704m, parsed.CostUsd);
        Assert.Equal("0df355fa-4f19-47ba-8f86-5b476f0e4080", parsed.SessionId);
    }

    // Two entries prove the fallback joins the whole array, not merely reads the first one.
    [Fact]
    public void TwoErrorsEntriesAreJoinedWithASemicolon()
    {
        JsonObject document = new()
        {
            ["type"] = "result",
            ["subtype"] = "error_during_execution",
            ["is_error"] = true,
            ["errors"] = new JsonArray("first problem", "second problem"),
            ["session_id"] = "synthetic-session",
        };

        ParsedOutput parsed = backend.Parse(document.ToJsonString(), stderr: "", exitCode: 1);

        Assert.Equal("first problem; second problem", parsed.FinalMessage);
    }

    // Neither `result` nor `errors` present: the last resort is the machine-readable `subtype`, which
    // at least names the failure class instead of leaving FinalMessage empty.
    [Fact]
    public void NeitherResultNorErrorsFallsBackToTheErrorSubtype()
    {
        JsonObject document = new()
        {
            ["type"] = "result",
            ["subtype"] = "error_during_execution",
            ["is_error"] = true,
            ["session_id"] = "synthetic-session",
        };

        ParsedOutput parsed = backend.Parse(document.ToJsonString(), stderr: "", exitCode: 1);

        Assert.Equal("error_during_execution", parsed.FinalMessage);
    }

    // A "success" subtype is not an `error_*` prefix, so the last-resort fallback must not fire on it
    // either: an empty `result` on an otherwise-successful document stays empty, not "success".
    [Fact]
    public void SuccessSubtypeWithEmptyResultStaysEmpty()
    {
        JsonObject document = new()
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = "",
            ["session_id"] = "synthetic-session",
        };

        ParsedOutput parsed = backend.Parse(document.ToJsonString(), stderr: "", exitCode: 0);

        Assert.Equal("", parsed.FinalMessage);
    }

    private static (string Stdout, string Stderr) ReadErrorFixture()
    {
        string[] lines = File.ReadAllLines(FixturePath("error.txt"));
        int stdoutHeader = Array.IndexOf(lines, "## STDOUT");
        int stderrHeader = Array.IndexOf(lines, "## STDERR");

        string stdout = string.Join('\n', lines[(stdoutHeader + 1)..stderrHeader]);
        string stderr = string.Join('\n', lines[(stderrHeader + 1)..]);
        return (stdout, stderr);
    }
}
