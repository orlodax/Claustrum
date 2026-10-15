using System.Text.Json;
using Claustrum.Coordination;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// #124 over MCP: every front door builds live requests, and the reply timeout is what says whether anyone can answer: 0 for `delegate`
// (it blocks its host, which cannot talk meanwhile), 1800 for `delegate_async` and `coordinate`. Asserted twice: on the request each
// tool's builder returns, and on the live.json a real run through the tool leaves (a conversing fake claude).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class LiveRequestTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string JobFile(string id, string name) => Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), id, name);

    [Theory]
    [InlineData(0)]
    [InlineData(DelegateEngine.DefaultReplyTimeoutSeconds)]
    public void TheDelegateRequestIsLiveWithTheReplyTimeoutItIsGiven(int replyTimeoutSeconds)
    {
        DelegateRequest request = ClaustrumTools.BuildRequest("builder", "x", repo.Repo, null, null, null, null, null, null, null, null, null, null, null, null, replyTimeoutSeconds);

        Assert.True(request.Live);
        Assert.Equal(replyTimeoutSeconds, request.ReplyTimeoutSeconds);
    }

    [Fact]
    public void TheCoordinateRequestIsLiveWithAnArchitectReplyWindowOfThirtyMinutes()
    {
        CoordinateRequest request = ClaustrumTools.BuildCoordinateRequest(null, "task", repo.Repo, null, null, null, null, null);

        Assert.True(request.Live);
        Assert.Equal(1800, request.ReplyTimeoutSeconds);
        Assert.Equal(DelegateEngine.DefaultReplyTimeoutSeconds, request.ReplyTimeoutSeconds);
    }

    [Fact]
    public async Task DelegateRunsLiveWithAZeroReplyTimeoutSoTheRoleIsToldNobodyIsThereAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        string json = await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: repo.Repo, cancellationToken: TestContext.Current.CancellationToken);

        using JsonDocument result = JsonDocument.Parse(json);
        Assert.Equal("success", result.RootElement.GetProperty("status").GetString());
        string id = result.RootElement.GetProperty("job_id").GetString() ?? "";
        using JsonDocument live = JsonDocument.Parse(File.ReadAllText(JobFile(id, "live.json")));
        Assert.Equal(0, live.RootElement.GetProperty("reply_timeout_seconds").GetInt32());
        string conversation = File.ReadAllText(JobFile(id, "conversation.jsonl"));
        Assert.Contains("No human is available to answer now.", conversation, StringComparison.Ordinal);
        Assert.Contains("stop with status needs_input", conversation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DelegateAsyncRunsLiveWithAThirtyMinuteReplyTimeoutAndTheQuestionReachesJobStatusAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("hold on"), FakeClaudeTurn.Report("fin")] });

        using JsonDocument started = JsonDocument.Parse(ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: repo.Repo));
        string id = started.RootElement.GetProperty("job_id").GetString() ?? "";

        await LiveJobHarness.WaitUntilAsync(() => ReadStatus(id) == "waiting_for_input", "the job to ask");
        using JsonDocument live = JsonDocument.Parse(File.ReadAllText(JobFile(id, "live.json")));
        Assert.Equal(1800, live.RootElement.GetProperty("reply_timeout_seconds").GetInt32());

        ClaustrumTools.JobSay(id, "go");
        await LiveJobHarness.WaitUntilAsync(() => ReadStatus(id) == "done", "the job to finish");
    }

    private static string ReadStatus(string id)
    {
        using JsonDocument status = JsonDocument.Parse(ClaustrumTools.JobStatus(id));
        return status.RootElement.GetProperty("state").GetString() ?? "";
    }
}
