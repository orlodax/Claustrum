using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// #126 over MCP, in-process: `delegate` returns the retry's receipt, and `delegate_async`'s job_status / job_result follow `retried_as` from the id
// the caller holds, so a question the retry asks shows under the first job's id. The cast's builder is refused by a fake opencode (the real 402) and
// falls back to the repo's fake claude.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class FallbackMcpTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string JobFile(string id, string name) => Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), id, name);

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private JsonElement Receipt(string id) => Parse(File.ReadAllText(JobFile(id, "result.json")));

    [Fact]
    public async Task DelegateReturnsTheRetrysReceiptWithFallbackOfAndTheWarningAsync()
    {
        FakeRefusals.Opencode opencode = FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback" });

        JsonElement result = Parse(await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: repo.Repo, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("success", Str(result, "status"));
        Assert.Equal("claude", Str(result, "backend"));
        string firstId = Str(result, "fallback_of");
        Assert.NotEqual(Str(result, "job_id"), firstId);
        Assert.Contains(result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()), text => text!.StartsWith("builder: opencode:fake/pro failed before starting (provider_quota:", StringComparison.Ordinal));
        Assert.Equal(Str(result, "job_id"), Str(Receipt(firstId), "retried_as"));
        Assert.Equal(1, opencode.Runs);
    }

    [Fact]
    public async Task DelegateWithAnExplicitModelIsNotRetriedAsync()
    {
        FakeRefusals.Opencode opencode = FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback", StdinLog = repo.StdinLog });

        JsonElement result = Parse(await ClaustrumTools.DelegateAsync(role: "builder", brief: "hi", cwd: repo.Repo, model: FallbackSetup.Primary, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("failed", Str(result, "status"));
        Assert.Equal("provider_quota", Str(result, "error_kind"));
        Assert.False(result.TryGetProperty("retried_as", out JsonElement retried) && retried.ValueKind != JsonValueKind.Null);
        Assert.Equal(1, opencode.Runs);
        Assert.False(File.Exists(repo.StdinLog));
    }

    [Fact]
    public async Task JobStatusFollowsRetriedAsSoTheRetrysQuestionShowsUnderTheFirstJobsIdAsync()
    {
        FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("which database?"), FakeClaudeTurn.Report("done on the fallback")] });

        string firstId = Str(Parse(ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: repo.Repo)), "job_id");

        // The job_status of the id the caller holds is that of the retry that took over.
        JsonElement status = default;
        await LiveJobHarness.WaitUntilAsync(() => (status = Parse(ClaustrumTools.JobStatus(firstId))).GetProperty("state").GetString() == "waiting_for_input", "the retry to ask");
        Assert.Equal("which database?", status.GetProperty("question").GetProperty("text").GetString());
        string retryId = Str(Receipt(firstId), "retried_as");
        Assert.NotEqual(firstId, retryId);
        Assert.Contains("which database?", File.ReadAllText(JobFile(retryId, "stdout.log")), StringComparison.Ordinal);

        Assert.Equal(1, Parse(ClaustrumTools.JobSay(retryId, "sqlite")).GetProperty("answers_question").GetInt32());
        await LiveJobHarness.WaitUntilAsync(() => Parse(ClaustrumTools.JobStatus(firstId)).GetProperty("state").GetString() == "done", "the job to finish");

        // job_result of the id the caller holds answers with the retry's receipt (as long as this server lives).
        JsonElement result = Parse(await ClaustrumTools.JobResultAsync(firstId));
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal(retryId, Str(result, "job_id"));
        Assert.Equal(firstId, Str(result, "fallback_of"));
        Assert.Equal("failed", Str(Receipt(firstId), "status"));
    }

    // #126 final review: job_status names the retry (retried_as) and job_say to the id the caller holds is taken by the retry, naming it; the retry's
    // own id, and a job that was never retried, carry no retried_as.
    [Fact]
    public async Task JobStatusReportsRetriedAsAndJobSayOnTheFirstIdReachesTheRetryAsync()
    {
        FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("which database?"), FakeClaudeTurn.Report("done on the fallback")] });
        string firstId = Str(Parse(ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: repo.Repo)), "job_id");

        JsonElement status = default;
        await LiveJobHarness.WaitUntilAsync(() => (status = Parse(ClaustrumTools.JobStatus(firstId))).GetProperty("state").GetString() == "waiting_for_input", "the retry to ask");
        string retryId = Str(Receipt(firstId), "retried_as");

        Assert.Equal(retryId, Str(status, "retried_as"));
        Assert.False(Parse(ClaustrumTools.JobStatus(retryId)).TryGetProperty("retried_as", out JsonElement own) && own.ValueKind != JsonValueKind.Null);

        // One hop, from the first id: the message is the retry's answer, and the result names the job that took it.
        JsonElement said = Parse(ClaustrumTools.JobSay(firstId, "sqlite"));
        Assert.Equal(1, said.GetProperty("answers_question").GetInt32());
        Assert.Equal(retryId, Str(said, "retried_as"));
        await LiveJobHarness.WaitUntilAsync(() => File.ReadAllText(JobFile(retryId, "conversation.jsonl")).Contains("sqlite", StringComparison.Ordinal), "the retry to take the answer");
        Assert.False(Directory.Exists(JobFile(firstId, "inbox")) && Directory.GetFiles(JobFile(firstId, "inbox")).Length > 0);
        await LiveJobHarness.WaitUntilAsync(() => Parse(ClaustrumTools.JobStatus(firstId)).GetProperty("state").GetString() == "done", "the job to finish");
    }

    [Fact]
    public async Task AJobThatWasNeverRetriedReportsNoRetriedAsAsync()
    {
        FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents(), fallback: null);
        repo.Script(new FakeClaudeScript { Summary = "x" });
        string jobId = Str(Parse(ClaustrumTools.DelegateStart(role: "builder", brief: "hi", cwd: repo.Repo)), "job_id");

        await LiveJobHarness.WaitUntilAsync(() => Parse(ClaustrumTools.JobStatus(jobId)).GetProperty("state").GetString() is "done" or "failed", "the job to end");

        JsonElement status = Parse(ClaustrumTools.JobStatus(jobId));
        Assert.False(status.TryGetProperty("retried_as", out JsonElement retried) && retried.ValueKind != JsonValueKind.Null);
        Assert.Equal("failed", Str(Receipt(jobId), "status"));
    }
}
