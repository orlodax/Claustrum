using System.Text.Json;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Json;

// AGENTS.md "All JSON goes through ClaustrumJsonContext" — the emitted-JSON contract from
// docs/PLAN.md §A2: snake_case properties, snake_case enum values (not UseStringEnumConverter's
// PascalCase default), schema_version "1".
public sealed class RunResultJsonContractTests
{
    private static RunResult MakeResult(RunStatus status = RunStatus.Success, ReportStatus reportStatus = ReportStatus.Ok) => new(
        SchemaVersion: "1", JobId: "20260913-120000-ab12", Status: status, Backend: "claude", Model: "sonnet",
        Role: "builder", FinalMessage: "done", ChangedFiles: [new ChangedFile("hello.txt", ChangeKind.Added)],
        Diff: "diff --git a/hello.txt b/hello.txt", DiffTruncated: false, SessionId: "sess-1", CostUsd: 0.01m,
        Usage: new Usage(10, 20, null, null), ExitCode: 0, LogPath: "/job/stdout.log", DurationSeconds: 1.5,
        Error: null, Raw: null, Report: null, ReportStatus: reportStatus, Warnings: []);

    [Fact]
    public void PropertiesSerializeAsSnakeCase()
    {
        string json = JsonSerializer.Serialize(MakeResult(), ClaustrumJsonContext.Default.RunResult);

        Assert.Contains("\"schema_version\":\"1\"", json);
        Assert.Contains("\"job_id\":", json);
        Assert.Contains("\"changed_files\":", json);
        Assert.Contains("\"diff_truncated\":", json);
        Assert.Contains("\"session_id\":", json);
        Assert.Contains("\"cost_usd\":", json);
        Assert.Contains("\"exit_code\":", json);
        Assert.Contains("\"log_path\":", json);
        Assert.Contains("\"duration_seconds\":", json);
        Assert.Contains("\"report_status\":", json);
    }

    [Fact]
    public void RunStatusEnumIsSnakeCaseNotPascalCase()
    {
        string json = JsonSerializer.Serialize(MakeResult(status: RunStatus.BackendMissing), ClaustrumJsonContext.Default.RunResult);

        Assert.Contains("\"status\":\"backend_missing\"", json);
        Assert.DoesNotContain("BackendMissing", json);
    }

    [Fact]
    public void ReportStatusEnumIsSnakeCase()
    {
        string json = JsonSerializer.Serialize(MakeResult(reportStatus: ReportStatus.Unparsed), ClaustrumJsonContext.Default.RunResult);

        Assert.Contains("\"report_status\":\"unparsed\"", json);
    }

    [Fact]
    public void ChangedFileKindUsesSingleLetterGitStyleCodes()
    {
        string json = JsonSerializer.Serialize(MakeResult(), ClaustrumJsonContext.Default.RunResult);

        Assert.Contains("\"kind\":\"A\"", json);
    }

    [Fact]
    public void SchemaVersionIsAlwaysTheStringOne()
    {
        string json = JsonSerializer.Serialize(MakeResult(), ClaustrumJsonContext.Default.RunResult);

        Assert.Contains("\"schema_version\":\"1\"", json);
    }

    [Fact]
    public void RoundTripsThroughDeserialization()
    {
        RunResult original = MakeResult();
        string json = JsonSerializer.Serialize(original, ClaustrumJsonContext.Default.RunResult);

        RunResult? roundTripped = JsonSerializer.Deserialize(json, ClaustrumJsonContext.Default.RunResult);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Status, roundTripped!.Status);
        Assert.Equal(original.ChangedFiles[0].Path, roundTripped.ChangedFiles[0].Path);
        Assert.Equal(original.ChangedFiles[0].Kind, roundTripped.ChangedFiles[0].Kind);
    }
}
