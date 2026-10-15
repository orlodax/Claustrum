using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Live;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// #124 over MCP: `job_status` reports a live job's `waiting_for_input` state with its `question`, and `abandoned`; `job_say`
// answers it. Job directories are hand-made under the shared fixture home (a job this process did not start is read from disk).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class LiveToolsTests(AppServicesHomeFixture fixture)
{
    private const int GonePid = int.MaxValue - 1;

    private string MakeJob(LiveStatus state, int? questionSeq, int pid, string role = "builder", params ConversationEntry[] entries)
    {
        string id = $"20261015-{Random.Shared.Next(100000, 999999)}-{Guid.NewGuid().ToString("N")[..8]}";
        string directory = Directory.CreateDirectory(Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), id)).FullName;
        File.WriteAllText(Path.Combine(directory, "request.json"), $$$"""{"role":"{{{role}}}"}""");
        File.WriteAllText(Path.Combine(directory, "live.json"), JsonSerializer.Serialize(new LiveState(state, questionSeq, DateTimeOffset.UtcNow, 60, pid), ClaustrumJsonContext.Default.LiveState));
        File.WriteAllLines(Path.Combine(directory, "conversation.jsonl"), entries.Select(entry => JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.ConversationEntry)));
        return id;
    }

    private static ConversationEntry Ask(int seq, string text) => new(seq, DateTimeOffset.UtcNow, ConversationParty.Role, ConversationKind.Question, text);

    [Fact]
    public void JobStatusOfAWaitingJobCarriesTheStateAndTheQuestion()
    {
        string id = MakeJob(LiveStatus.WaitingForInput, 1, Environment.ProcessId, entries: Ask(1, "which one?"));

        using JsonDocument status = JsonDocument.Parse(ClaustrumTools.JobStatus(id));

        Assert.Equal("waiting_for_input", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, status.RootElement.GetProperty("question").GetProperty("seq").GetInt32());
        Assert.Equal("which one?", status.RootElement.GetProperty("question").GetProperty("text").GetString());
    }

    [Fact]
    public void JobStatusOfARunningLiveJobHasNoQuestionAndOfADeadOwnerIsAbandoned()
    {
        string running = MakeJob(LiveStatus.Running, null, Environment.ProcessId);
        string abandoned = MakeJob(LiveStatus.Running, null, GonePid);

        using JsonDocument runningStatus = JsonDocument.Parse(ClaustrumTools.JobStatus(running));
        using JsonDocument abandonedStatus = JsonDocument.Parse(ClaustrumTools.JobStatus(abandoned));

        Assert.Equal("running", runningStatus.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, runningStatus.RootElement.GetProperty("question").ValueKind);
        Assert.Equal("abandoned", abandonedStatus.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void JobSayAnswersTheOpenQuestionAndWritesItToTheInbox()
    {
        string id = MakeJob(LiveStatus.WaitingForInput, 1, Environment.ProcessId, entries: Ask(1, "which one?"));

        using JsonDocument said = JsonDocument.Parse(ClaustrumTools.JobSay(id, "the red one"));

        Assert.Equal(1, said.RootElement.GetProperty("answers_question").GetInt32());
        Assert.Equal("waiting_for_input", said.RootElement.GetProperty("state").GetString());
        string file = Assert.Single(Directory.GetFiles(Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), id, "inbox"), "*.json"));
        Assert.Contains("the red one", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("code-reviewer", "blind")]
    [InlineData("abandoned", "abandoned")]
    public void JobSayIsRefusedForABlindRoleOrAnAbandonedJob(string kind, string reason)
    {
        string id = kind == "abandoned" ? MakeJob(LiveStatus.Running, null, GonePid) : MakeJob(LiveStatus.Running, null, Environment.ProcessId, role: kind);

        McpException refused = Assert.Throws<McpException>(() => ClaustrumTools.JobSay(id, "hello"));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JobSayToAnUnknownJobIsAnError()
    {
        Assert.Throws<McpException>(() => ClaustrumTools.JobSay("does-not-exist-" + Guid.NewGuid(), "hello"));
    }
}
