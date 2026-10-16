using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// #130 through the other front door, over real stdio against a real `claustrum mcp` and a fake `opencode acp`: `delegate_async` starts a live ACP job in the server's
// own process, `job_status` shows its question, `job_say` answers it (the next `session/prompt` of the same agent), and `job_result` is the receipt; `delegate` blocks for a
// batch run. The configured path is what runs and the server's PATH holds no opencode.
public sealed class AcpMcpEndToEndTests : IDisposable
{
    private static readonly TimeSpan limit = TimeSpan.FromSeconds(30);

    private readonly IsolatedRepo repo = new();

    public AcpMcpEndToEndTests() => FakeAcpAgent.RequirePosixShell();

    public void Dispose() => repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeAcpAgent Fake(params FakeAcpTurn[] turns)
    {
        FakeAcpAgent fake = new(Directory.CreateDirectory(Path.Combine(repo.Home, "fake-acp")).FullName, turns);
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{" + fake.BackendJson + "}}");
        return fake;
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

    private static JsonObject Arguments(string brief) => new() { ["role"] = "builder", ["brief"] = brief, ["backend"] = "acp-opencode", ["model"] = "opencode/fake" };

    private static async Task<string> WaitForStateAsync(McpStdioClient client, string id, string state)
    {
        DateTime due = DateTime.UtcNow + limit;
        while (true)
        {
            using JsonDocument status = await CallAsync(client, "job_status", new JsonObject { ["jobId"] = id });
            if (status.RootElement.GetProperty("state").GetString() == state)
                return status.RootElement.GetRawText();

            Assert.True(DateTime.UtcNow < due, $"timed out waiting for state '{state}': {status.RootElement.GetRawText()}");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task DelegateAsyncStartsALiveAcpJobJobStatusShowsItsQuestionJobSayAnswersItAndJobResultIsTheReceiptAsync()
    {
        FakeAcpAgent fake = Fake(FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it", "ready"));
        await using McpStdioClient client = await ConnectAsync();

        using JsonDocument started = await CallAsync(client, "delegate_async", Arguments("do the task"));
        string id = started.RootElement.GetProperty("job_id").GetString() ?? "";
        string waiting = await WaitForStateAsync(client, id, "waiting_for_input");
        using JsonDocument status = JsonDocument.Parse(waiting);
        Assert.Contains("which database?", status.RootElement.GetProperty("question").GetRawText(), StringComparison.Ordinal);

        using JsonDocument said = await CallAsync(client, "job_say", new JsonObject { ["jobId"] = id, ["text"] = "sqlite" });
        await WaitForStateAsync(client, id, "done");
        using JsonDocument result = await CallAsync(client, "job_result", new JsonObject { ["jobId"] = id });

        Assert.Equal("success", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("acp-opencode", result.RootElement.GetProperty("backend").GetString());
        Assert.Equal(0.02m, result.RootElement.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(FakeAcpAgent.Session, result.RootElement.GetProperty("session_id").GetString());
        Assert.Equal(1, fake.Runs);
        Assert.Equal(2, fake.Prompts);
        Assert.Equal("sqlite", fake.PromptText(2));
        Assert.Equal(["acp"], fake.Argv(1));
    }

    [Fact]
    public async Task DelegateBlocksForABatchAcpRunAndReturnsItsReceiptAsync()
    {
        FakeAcpAgent fake = Fake(FakeAcpTurn.Report("done", "finished", costUsd: 0.03m));
        await using McpStdioClient client = await ConnectAsync();

        using JsonDocument result = await CallAsync(client, "delegate", Arguments("do the task"));

        Assert.Equal("success", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("acp-opencode", result.RootElement.GetProperty("backend").GetString());
        Assert.Equal(0.03m, result.RootElement.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(1, fake.Prompts);
    }

    [Fact]
    public async Task DelegateRefusesAClaudeOnlyRoleOnAcpOpencodeAsAToolErrorNamingBothNamesAsync()
    {
        Fake(FakeAcpTurn.Report("never reached"));
        await using McpStdioClient client = await ConnectAsync();
        JsonObject arguments = Arguments("x");
        arguments["role"] = "ui-reviewer";

        (bool isError, string text) = await client.CallToolAsync("delegate", arguments, Ct);

        Assert.True(isError);
        Assert.Contains("'acp-opencode' (harness 'opencode')", text, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
    }
}
