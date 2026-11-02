using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Live;
using Claustrum.Mcp.Json;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #145: the small joints between the receipt and the doors that read it: the status's exit code, the retry that must not happen, the job status MCP
// and `jobs wait --json` carry, the doctor's line. AppServicesHomeFixture keeps the jobs these touch out of the real ~/.claustrum/jobs.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class EnvironmentIssuesPlumbingTests(AppServicesHomeFixture fixture)
{
    private static readonly EnvironmentIssue issue = new(EnvironmentIssueKind.ExternalDirectory, "Permission denied: external_directory", "/srv/shared", 2, "stdout.log:9");

    private static RunResult Result(RunStatus status = RunStatus.Success, EnvironmentIssue[]? issues = null, string jobId = "job-1") => new(
        SchemaVersion: "1", JobId: jobId, Status: status, Backend: "claude", Model: "opus", Role: "builder", FinalMessage: "ok", ChangedFiles: [], Diff: null,
        DiffTruncated: false, SessionId: null, CostUsd: 0.0123m, Usage: null, ExitCode: 0, LogPath: "log", DurationSeconds: 1.5, Error: null, Raw: null, Report: null,
        ReportStatus: ReportStatus.Missing, Warnings: [], EnvironmentIssues: issues);

    // ---- the exit code -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RunStatus.Success, 0)]
    [InlineData(RunStatus.SuccessWithIssues, 8)]
    [InlineData(RunStatus.NeedsInput, 7)]
    public void SuccessWithIssuesExitsEightAndNoOtherStatusMovedItsCode(RunStatus status, int code) => Assert.Equal(code, RunCommand.ExitCodeFor(status));

    [Fact]
    public void EveryStatusHasAnExitCodeAndEightBelongsToSuccessWithIssuesAlone()
    {
        Dictionary<RunStatus, int> codes = Enum.GetValues<RunStatus>().ToDictionary(status => status, RunCommand.ExitCodeFor);

        Assert.Equal(ExitCodes.EnvironmentIssues, codes[RunStatus.SuccessWithIssues]);
        Assert.Equal([RunStatus.SuccessWithIssues], [.. codes.Where(pair => pair.Value == 8).Select(pair => pair.Key)]);
    }

    [Fact]
    public void TheStatusIsSnakeCaseOnTheWireAndTheIssuesTooAndAReceiptWithNoIssuesOmitsNothingItNeeds()
    {
        string json = JsonSerializer.Serialize(Result(RunStatus.SuccessWithIssues, [issue]), ClaustrumJsonContext.Default.RunResult);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("success_with_issues", document.RootElement.GetProperty("status").GetString());
        JsonElement written = document.RootElement.GetProperty("environment_issues")[0];
        Assert.Equal(["kind", "what", "where", "count", "first"], written.EnumerateObject().Select(property => property.Name));
        Assert.Equal("external_directory", written.GetProperty("kind").GetString());

        RunResult back = JsonSerializer.Deserialize(json, ClaustrumJsonContext.Default.RunResult)!;
        Assert.Equal(RunStatus.SuccessWithIssues, back.Status);
        Assert.Equal([issue], back.EnvironmentIssues!);
    }

    // ---- the cast's fallback never retries a run that finished ---------------------------------------------------------------------------------

    private static RunResult Refusal(RunStatus status, EnvironmentIssue[]? issues = null) => Result(status, issues) with
    {
        BeforeAnyWork = true,
        ErrorKind = ErrorKind.ProviderQuota,
        Error = "credits",
    };

    [Fact]
    public void ASuccessWithIssuesRunIsNeverRetriedWhateverElseItLooksLike()
    {
        Assert.False(FallbackRetry.Qualifies(Refusal(RunStatus.SuccessWithIssues, [issue])));
        Assert.False(FallbackRetry.Qualifies(Refusal(RunStatus.SuccessWithIssues)));
    }

    [Fact]
    public void AFailedProviderRefusalThatListsDispatchIssuesStillQualifiesForTheRetry()
    {
        Assert.True(FallbackRetry.Qualifies(Refusal(RunStatus.Failed, [issue])));
    }

    // ---- job_status (MCP) and the job manager -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFinishedJobsStatusCarriesItsEnvironmentIssuesAndACleanOneCarriesNoneAsync()
    {
        JobManager manager = new();

        (string dirty, _) = manager.Start((job, _) => Task.FromResult(Result(RunStatus.SuccessWithIssues, [issue], job.Id)), CancellationToken.None);
        (string clean, _) = manager.Start((job, _) => Task.FromResult(Result(jobId: job.Id)), CancellationToken.None);
        await manager.GetResultAsync(dirty);
        await manager.GetResultAsync(clean);

        JobStatusInfo? withIssues = manager.GetStatus(dirty);
        Assert.Equal("done", withIssues?.State);
        Assert.Equal("success_with_issues", withIssues?.Status);
        Assert.Equal([issue], withIssues?.EnvironmentIssues!);
        Assert.Null(manager.GetStatus(clean)?.EnvironmentIssues);
        Assert.Equal("success", manager.GetStatus(clean)?.Status);
    }

    [Fact]
    public async Task AJobOnDiskFromAnotherProcessAnswersWithItsIssuesTooAsync()
    {
        JobManager manager = new();
        string jobId = $"20261102-100000-{Guid.NewGuid().ToString("N")[..8]}";
        string directory = Path.Combine(Core.Jobs.JobDirectory.ResolveRoot(fixture.Platform), jobId);
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(Result(RunStatus.SuccessWithIssues, [issue], jobId), ClaustrumJsonContext.Default.RunResult), TestContext.Current.CancellationToken);

            JobStatusInfo? status = manager.GetStatus(jobId);

            Assert.Equal("done", status?.State);
            Assert.Equal("success_with_issues", status?.Status);
            Assert.Equal([issue], status?.EnvironmentIssues!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheJobStatusToolSerializesEnvironmentIssuesInSnakeCase()
    {
        string json = JsonSerializer.Serialize(new JobStatusInfo("done", 0, null, Status: "success_with_issues", EnvironmentIssues: [issue]), McpJsonContext.Default.JobStatusInfo);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement written = document.RootElement.GetProperty("environment_issues")[0];
        Assert.Equal("success_with_issues", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("external_directory", written.GetProperty("kind").GetString());
        Assert.Equal("/srv/shared", written.GetProperty("where").GetString());
        Assert.Equal(2, written.GetProperty("count").GetInt32());
        Assert.Equal("stdout.log:9", written.GetProperty("first").GetString());
    }

    // ---- jobs wait --json ----------------------------------------------------------------------------------------------------------------------

    private static JsonElement Receipt(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ObservingAFinishedReceiptCopiesItsIssuesAndOnlyWhenItListsAny()
    {
        JsonElement dirty = Receipt(/*lang=json,strict*/ """{"status":"success_with_issues","environment_issues":[{"kind":"auth","what":"HTTP 404 from GitHub","where":"x","count":1,"first":"stdout.log:3"}]}""");

        Assert.Equal("auth", LiveJob.IssuesOf(dirty)!.Value[0].GetProperty("kind").GetString());
        Assert.Null(LiveJob.IssuesOf(Receipt(/*lang=json,strict*/ """{"status":"success","environment_issues":[]}""")));
        Assert.Null(LiveJob.IssuesOf(Receipt(/*lang=json,strict*/ """{"status":"success","environment_issues":null}""")));
        Assert.Null(LiveJob.IssuesOf(Receipt(/*lang=json,strict*/ """{"status":"success"}""")));
        Assert.Null(LiveJob.IssuesOf(Receipt(/*lang=json,strict*/ """{"status":"success","environment_issues":{"kind":"auth"}}""")));
        Assert.Equal("success_with_issues", LiveJob.StatusOf(dirty));
    }

    [Fact]
    public void TheWaitDocumentHasEnvironmentIssuesAsItsFourthKeyAndNullWhileTheJobRunsOrListsNone()
    {
        JsonElement issues = Receipt(/*lang=json,strict*/ """[{"kind":"auth","count":1}]""");

        string done = JsonSerializer.Serialize(new JobWaitResult("done", null, "success_with_issues", issues), LiveJsonContext.Default.JobWaitResult);
        string running = JsonSerializer.Serialize(new JobWaitResult("running", null, null), LiveJsonContext.Default.JobWaitResult);

        using JsonDocument doneDocument = JsonDocument.Parse(done);
        using JsonDocument runningDocument = JsonDocument.Parse(running);
        Assert.Equal(["state", "question", "result_status", "environment_issues"], doneDocument.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("auth", doneDocument.RootElement.GetProperty("environment_issues")[0].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, runningDocument.RootElement.GetProperty("environment_issues").ValueKind);
    }

    // ---- claustrum doctor's probe --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDoctorsProbeCallsASuccessWithIssuesOkAndSaysWhereToRead()
    {
        string described = DoctorProbe.Describe(Result(RunStatus.SuccessWithIssues, [issue, issue], "20261102-100000-abcdef12"));

        Assert.Equal("OK, with 2 environment issue(s) (1.5s, cost $0.0123) — claustrum jobs show 20261102-100000-abcdef12", described);
        Assert.StartsWith("OK (", DoctorProbe.Describe(Result()), StringComparison.Ordinal);
    }
}
