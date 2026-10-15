using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// #124 stage 3b over real stdio against a real `claustrum mcp` and a fake `claude` (IsolatedRepo): a needs_input job shows its questions in job_status
// and job_result, and job_answer continues it as a linked job, once. The MCP surface is the front door a host agent has; the CLI is covered by NeedsInputEndToEndTests.
public sealed class JobAnswerMcpEndToEndTests : IDisposable
{
    private const string Asks =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":["sqlite","postgres"],"allow_free_form":false}]}""";

    private static readonly TimeSpan resultTimeout = TimeSpan.FromSeconds(30);

    private readonly IsolatedRepo repo = new();

    public JobAnswerMcpEndToEndTests() => FakeClaude.RequirePosixShell();

    public void Dispose() => repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> AskedAsync()
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { ReportJson = Asks, SessionId = "mcp-session" });
        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--no-live");
        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        return result.GetProperty("job_id").GetString() ?? "";
    }

    private async Task<McpStdioClient> ConnectAsync()
    {
        McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);
        return client;
    }

    private static async Task<JsonDocument> CallAsync(McpStdioClient client, string tool, JsonObject arguments)
    {
        (bool isError, string text) = await client.CallToolAsync(tool, arguments, Ct);
        Assert.False(isError, text);
        return JsonDocument.Parse(text);
    }

    [Fact]
    public async Task JobStatusAndJobResultOfANeedsInputJobCarryItsQuestionsAsync()
    {
        string id = await AskedAsync();
        await using McpStdioClient client = await ConnectAsync();

        using JsonDocument status = await CallAsync(client, "job_status", new JsonObject { ["jobId"] = id });
        using JsonDocument result = await CallAsync(client, "job_result", new JsonObject { ["jobId"] = id });

        Assert.Equal("done", status.RootElement.GetProperty("state").GetString());
        Assert.Equal("needs_input", status.RootElement.GetProperty("status").GetString());
        JsonElement question = Assert.Single(status.RootElement.GetProperty("questions").EnumerateArray());
        Assert.Equal("db", question.GetProperty("key").GetString());
        Assert.Equal("Which database?", question.GetProperty("prompt").GetString());
        Assert.Equal("needs_input", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("db", Assert.Single(result.RootElement.GetProperty("questions").EnumerateArray()).GetProperty("key").GetString());
    }

    [Fact]
    public async Task JobAnswerStartsALinkedJobAtOnceAndThePolledResultIsTheContinuationsAsync()
    {
        string id = await AskedAsync();
        repo.Script(new FakeClaudeScript { Summary = "finished", SessionId = "mcp-session", ArgvLog = repo.ArgvLog, StdinLog = repo.StdinLog });
        await using McpStdioClient client = await ConnectAsync();

        using JsonDocument started = await CallAsync(client, "job_answer", new JsonObject { ["jobId"] = id, ["answers"] = new JsonObject { ["db"] = "sqlite" } });

        string next = started.RootElement.GetProperty("job_id").GetString() ?? "";
        Assert.NotEqual(id, next);
        Assert.Equal(id, started.RootElement.GetProperty("parent_job_id").GetString());
        Assert.Contains("stdout.log", started.RootElement.GetProperty("log_path").GetString(), StringComparison.Ordinal);
        await DetachedRepo.WaitUntilAsync(() => File.Exists(Path.Combine(repo.JobsRoot, next, "result.json")), $"the receipt of {next}", resultTimeout);
        await Task.Delay(100, Ct);
        Assert.Equal("success", repo.ReadResult(next).GetProperty("status").GetString());
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal("mcp-session", argv[Array.IndexOf(argv, "--resume") + 1]);
        Assert.Contains("sqlite", File.ReadAllText(repo.StdinLog), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobAnswerOnAJobAlreadyAnsweredOrNotAskingIsAToolErrorAsync()
    {
        string id = await AskedAsync();
        repo.Script(new FakeClaudeScript { Summary = "finished", SessionId = "mcp-session" });
        await using McpStdioClient client = await ConnectAsync();
        using JsonDocument started = await CallAsync(client, "job_answer", new JsonObject { ["jobId"] = id, ["text"] = "sqlite" });
        string next = started.RootElement.GetProperty("job_id").GetString() ?? "";
        await DetachedRepo.WaitUntilAsync(() => File.Exists(Path.Combine(repo.JobsRoot, next, "result.json")), $"the receipt of {next}", resultTimeout);

        (bool again, string againText) = await client.CallToolAsync("job_answer", new JsonObject { ["jobId"] = id, ["text"] = "postgres" }, Ct);
        (bool notAsking, string notAskingText) = await client.CallToolAsync("job_answer", new JsonObject { ["jobId"] = next, ["text"] = "x" }, Ct);
        (bool neither, string neitherText) = await client.CallToolAsync("job_answer", new JsonObject { ["jobId"] = id }, Ct);

        Assert.True(again);
        Assert.Contains($"job '{id}' was already answered", againText, StringComparison.Ordinal);
        Assert.Contains(next, againText, StringComparison.Ordinal);
        Assert.True(notAsking);
        Assert.Contains("not needs_input", notAskingText, StringComparison.Ordinal);
        Assert.True(neither);
        Assert.Contains("already answered", neitherText, StringComparison.Ordinal);
    }
}
