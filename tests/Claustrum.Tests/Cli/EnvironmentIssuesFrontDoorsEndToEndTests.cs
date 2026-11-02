using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #144, #146, #145: every front door that starts a job hands it the same dispatch result. A missing read dir in the user config is the probe: it must reach
// the receipt first, as `first: dispatch`, and the warning must reach stderr or the receipt, through `run`, a detached run, `jobs answer`, the cast's
// fallback retry, MCP `delegate` and `delegate_async`, and `coordinate`'s architect. A fake claude stands for the backend: the child's PATH holds none.
public sealed class EnvironmentIssuesFrontDoorsEndToEndTests : IDisposable
{
    private const string Session = "sess-1";

    private const string AsksOneQuestion =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":["sqlite","postgres"],"allow_free_form":false}]}""";

    private static readonly TimeSpan limit = TimeSpan.FromSeconds(30);

    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-doors-").FullName;

    // POSIX only: the fakes that record, hold a conversation or run under a detached host are shell scripts.
    public EnvironmentIssuesFrontDoorsEndToEndTests() => FakeClaude.RequirePosixShell();

    public void Dispose() => TempTree.Delete(scratch);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Missing => Path.Combine(scratch, "no-such-dir");

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private void WriteUserConfig(IsolatedRepo repo) => UserConfig.Write(
        repo.Home,
        "{\"models\":{\"frontier-reasoning\":\"claude:opus\",\"frontier-coding\":\"claude:opus\",\"standard-coding\":\"claude:sonnet\",\"cheap-coding\":\"claude:haiku\",\"fast\":\"claude:haiku\"},"
        + "\"read_dirs\":[" + JsonSerializer.Serialize(Missing) + "]}");

    // A refusal's receipt lists the issues dispatch found; the dispatch warnings were said on stderr as it began and are not repeated in it.
    private void AssertDispatchIssue(JsonElement receipt, string expectedStatus = "success_with_issues", bool withWarning = true)
    {
        Assert.Equal(expectedStatus, Str(receipt, "status"));
        JsonElement issue = receipt.GetProperty("environment_issues")[0];
        Assert.Equal("missing_directory", Str(issue, "kind"));
        Assert.Equal(Missing, Str(issue, "where"));
        Assert.Equal("dispatch", Str(issue, "first"));
        if (withWarning)
            Assert.Equal($"read_dirs: {Missing} does not exist; the job runs without it", receipt.GetProperty("warnings")[0].GetString());
    }

    // ---- run --detach --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADetachedRunsHostedReceiptCarriesTheDispatchIssueAsync()
    {
        using DetachedRepo fixture = new();
        WriteUserConfig(fixture.Repo);
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("hosted")] });

        CliResult process = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        using JsonDocument started = JsonDocument.Parse(process.Stdout);
        string id = Str(started.RootElement, "job_id");
        await fixture.WaitForResultAsync(id);
        JsonElement receipt = fixture.Json(id, "result.json") ?? throw new InvalidOperationException("no receipt");
        AssertDispatchIssue(receipt);
        await fixture.WaitForHostGoneAsync(id);

        CliResult wait = await fixture.Repo.RunAsync("jobs", "wait", id, "--json");
        using JsonDocument waited = JsonDocument.Parse(wait.Stdout);
        Assert.Equal("success_with_issues", Str(waited.RootElement, "result_status"));
        Assert.Contains("warning: environment issues (1): missing_directory", wait.Stderr, StringComparison.Ordinal);
    }

    // ---- jobs answer ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnswerContinuesTheJobAndTheContinuationGetsTheDispatchIssueTooAsync()
    {
        using IsolatedRepo repo = new();
        WriteUserConfig(repo);
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { ReportJson = AsksOneQuestion, SessionId = Session });
        (CliResult asked, JsonElement first) = await repo.RunBuilderAsync("--no-live");
        Assert.Equal(ExitCodes.NeedsInput, asked.ExitCode);
        AssertDispatchIssue(first, "needs_input");

        repo.Script(new FakeClaudeScript { Summary = "finished", SessionId = Session });
        CliResult answered = await repo.RunAsync("jobs", "answer", Str(first, "job_id"), "sqlite", "--json");

        Assert.Equal(ExitCodes.EnvironmentIssues, answered.ExitCode);
        using JsonDocument continued = JsonDocument.Parse(answered.Stdout);
        AssertDispatchIssue(continued.RootElement);
        Assert.NotEqual(Str(first, "job_id"), Str(continued.RootElement, "job_id"));
        Assert.Contains($"read_dirs: {Missing} does not exist", answered.Stderr, StringComparison.Ordinal);
    }

    // ---- the cast's fallback retry -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFallbackRetryIsPreparedAfreshAndItsReceiptCarriesTheDispatchIssueAsync()
    {
        using IsolatedRepo repo = new();
        WriteUserConfig(repo);
        FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback" });

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "Review the diff.\n\n## Diff\n+ a line\n", "--json");

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        JsonElement retry = document.RootElement;
        Assert.Equal("claude", Str(retry, "backend"));
        Assert.True(retry.TryGetProperty("fallback_of", out JsonElement of) && of.ValueKind == JsonValueKind.String);
        Assert.Equal("missing_directory", Str(retry.GetProperty("environment_issues")[0], "kind"));
        Assert.Equal("dispatch", Str(retry.GetProperty("environment_issues")[0], "first"));

        // The refused first job was dispatched with it too: opencode honours read dirs, so its receipt names the same missing dir.
        JsonElement first = repo.ReadResult(Str(retry, "fallback_of"));
        Assert.Equal("failed", Str(first, "status"));
        Assert.Equal("dispatch", Str(first.GetProperty("environment_issues")[0], "first"));
    }

    // ---- the worktree hook: after a worktree is cut, checked out on a branch, or reopened for a continuation -----------------------------------------

    private string WriteHookConfig(IsolatedRepo repo)
    {
        string marker = Path.Combine(scratch, "hook.marker");
        UserConfig.Write(
            repo.Home,
            "{\"models\":{\"frontier-reasoning\":\"claude:opus\",\"frontier-coding\":\"claude:opus\",\"standard-coding\":\"claude:sonnet\",\"cheap-coding\":\"claude:haiku\",\"fast\":\"claude:haiku\"},"
            + "\"projects\":{\"*\":{\"worktree_hook\":[\"sh\",\"-c\",\"printf '%s\\\\n' \\\"$1\\\" >> " + marker + "\",\"sh\",\"{worktree}\"]}}}");
        return marker;
    }

    [Fact]
    public async Task TheHookRunsAgainWhenABranchRunChecksTheWorktreeOutAsync()
    {
        using IsolatedRepo repo = new();
        string marker = WriteHookConfig(repo);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "added first.txt" });
        (_, JsonElement first) = await repo.RunBuilderAsync();
        Assert.Single(File.ReadAllLines(marker));

        (CliResult second, JsonElement continued) = await repo.RunBuilderAsync("--branch", Str(first, "branch"));

        Assert.Equal(ExitCodes.Ok, second.ExitCode);
        string[] ran = File.ReadAllLines(marker);
        Assert.Equal(2, ran.Length);
        Assert.EndsWith(IsolatedRepo.WorktreeTail(Str(first, "job_id")), ran[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(IsolatedRepo.WorktreeTail(Str(continued, "job_id")), ran[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheHookRunsAgainWhenAnAnswerReopensTheJobsWorktreeAsync()
    {
        using IsolatedRepo repo = new();
        string marker = WriteHookConfig(repo);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { ReportJson = AsksOneQuestion, SessionId = Session });
        (CliResult asked, JsonElement first) = await repo.RunBuilderAsync("--no-live");
        Assert.Equal(ExitCodes.NeedsInput, asked.ExitCode);
        Assert.Single(File.ReadAllLines(marker));

        repo.Script(new FakeClaudeScript { Summary = "finished", SessionId = Session });
        CliResult answered = await repo.RunAsync("jobs", "answer", Str(first, "job_id"), "sqlite", "--json");

        Assert.Equal(ExitCodes.Ok, answered.ExitCode);
        string[] ran = File.ReadAllLines(marker);
        Assert.Equal(2, ran.Length);
        Assert.All(ran, line => Assert.EndsWith(IsolatedRepo.WorktreeTail(Str(first, "job_id")), line, StringComparison.OrdinalIgnoreCase));
    }

    // ---- a refusal is a receipt too, and carries what dispatch found ----------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusalBeforeTheJobStartsStillListsTheDispatchIssueOnItsFailedReceiptAsync()
    {
        using IsolatedRepo repo = new();
        WriteUserConfig(repo);
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Summary = "ok" });

        (CliResult process, JsonElement receipt) = await repo.RunBuilderAsync("--branch", "claustrum/no-such-branch");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Contains("no-such-branch", Str(receipt, "error"), StringComparison.Ordinal);
        AssertDispatchIssue(receipt, "failed", withWarning: false);
        AssertDispatchIssue(repo.ReadResult(Str(receipt, "job_id")), "failed", withWarning: false);
        Assert.Contains("warning: environment issues (1): missing_directory", process.Stderr, StringComparison.Ordinal);
    }

    // ---- MCP -------------------------------------------------------------------------------------------------------------------------------

    private static async Task<JsonDocument> CallAsync(McpStdioClient client, string tool, JsonObject arguments)
    {
        (bool isError, string text) = await client.CallToolAsync(tool, arguments, Ct);
        Assert.False(isError, text);
        return JsonDocument.Parse(text);
    }

    [Fact]
    public async Task McpDelegateReturnsSuccessWithIssuesAndTheDispatchIssueAsync()
    {
        using IsolatedRepo repo = new();
        WriteUserConfig(repo);
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        using JsonDocument result = await CallAsync(client, "delegate", new JsonObject { ["role"] = "builder", ["brief"] = "do the task" });

        AssertDispatchIssue(result.RootElement);
    }

    [Fact]
    public async Task McpJobStatusOfAFinishedAsyncJobCarriesEnvironmentIssuesAndAStatusThatSaysSoAsync()
    {
        using IsolatedRepo repo = new();
        WriteUserConfig(repo);
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        using JsonDocument started = await CallAsync(client, "delegate_async", new JsonObject { ["role"] = "builder", ["brief"] = "do the task" });
        string id = Str(started.RootElement, "job_id");
        JsonDocument? status = null;
        DateTime deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            status?.Dispose();
            status = await CallAsync(client, "job_status", new JsonObject { ["jobId"] = id });
            if (Str(status.RootElement, "state") == "done")
                break;

            await Task.Delay(200, Ct);
        }

        using (status)
        {
            Assert.NotNull(status);
            Assert.Equal("done", Str(status.RootElement, "state"));
            Assert.Equal("success_with_issues", Str(status.RootElement, "status"));
            JsonElement issue = status.RootElement.GetProperty("environment_issues")[0];
            Assert.Equal("missing_directory", Str(issue, "kind"));
            Assert.Equal("dispatch", Str(issue, "first"));
        }

        using JsonDocument receipt = await CallAsync(client, "job_result", new JsonObject { ["jobId"] = id });
        AssertDispatchIssue(receipt.RootElement);
    }

    [Fact]
    public async Task McpJobStatusOfACleanJobHasNoEnvironmentIssuesAsync()
    {
        using IsolatedRepo repo = new();
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        (_, JsonElement result) = await repo.RunBuilderAsync();
        await using McpStdioClient client = repo.StartMcp();
        await client.InitializeAsync(Ct);

        using JsonDocument status = await CallAsync(client, "job_status", new JsonObject { ["jobId"] = Str(result, "job_id") });

        Assert.Equal("success", Str(status.RootElement, "status"));
        Assert.True(!status.RootElement.TryGetProperty("environment_issues", out JsonElement issues) || issues.ValueKind == JsonValueKind.Null);
    }

    // ---- coordinate's architect ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CoordinatesArchitectReceiptCarriesTheDispatchIssueAndCoordinatePrintsTheLineAsync()
    {
        using IsolatedRepo repo = IsolatedRepo.ForCoordinate();
        WriteUserConfig(repo);
        repo.Script(new FakeClaudeScript { Summary = "integrated" });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        Assert.Equal("architect", Str(result, "role"));
        AssertDispatchIssue(result);
        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        Assert.Contains("warning: environment issues (1): missing_directory", process.Stderr, StringComparison.Ordinal);
        AssertDispatchIssue(repo.ReadResult(Str(result, "job_id")));
    }
}
