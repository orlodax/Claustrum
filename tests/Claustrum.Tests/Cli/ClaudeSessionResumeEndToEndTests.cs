using System.Globalization;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #127 final review, findings 1 and 2, through the built binary against a fake `claude` that now honours --no-session-persistence
// (FakeClaude: a session started with the flag cannot be resumed, "No conversation found"): a non-blind role with a report keeps its
// session, so `jobs answer` continues a needs_input job, and a continuation is charged only what it added (NOTES.md "Final review of #127").
public sealed class ClaudeSessionResumeEndToEndTests : IDisposable
{
    private const string Ask =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped on a question","questions":[{"key":"db","prompt":"Which database?","options":[],"allow_free_form":true}]}""";

    private const string Session = "sess-keep";

    private readonly IsolatedRepo repo = new();

    public ClaudeSessionResumeEndToEndTests() => FakeClaude.RequirePosixShell();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private string FakeDirectory => Path.GetDirectoryName(repo.ScriptPath) ?? throw new InvalidOperationException(repo.ScriptPath);

    private string[] Argv() => File.ReadAllLines(repo.ArgvLog);

    private static decimal Cost(JsonElement receipt) => receipt.GetProperty("cost_usd").GetDecimal();

    private async Task<string> AskAsync(decimal total, bool recordArgv = false)
    {
        repo.WriteCast(null);
        repo.Script(new FakeClaudeScript { ReportJson = Ask, SessionId = Session, TotalCostUsd = total, ArgvLog = recordArgv ? repo.ArgvLog : null });
        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--no-live");
        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        return Str(result, "job_id");
    }

    private async Task<(CliResult Process, string JobId)> AnswerAsync(string jobId, FakeClaudeScript script)
    {
        repo.Script(script);
        CliResult process = await repo.RunAsync("jobs", "answer", jobId, "sqlite", "--json");
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, Str(document.RootElement, "job_id"));
    }

    // ---- finding 1: the session of a job that can ask is saved -----------------------------------------------------------------

    [Fact]
    public async Task ANonBlindRoleWithAReportIsLaunchedWithoutNoSessionPersistenceAsync()
    {
        await AskAsync(0.10m, recordArgv: true);

        Assert.DoesNotContain("--no-session-persistence", Argv());
    }

    [Fact]
    public async Task ABlindRoleKeepsNoSessionPersistenceAsync()
    {
        repo.WriteCast(null, role: "code-reviewer");
        repo.Script(new FakeClaudeScript { Summary = "fine", SessionId = Session, ArgvLog = repo.ArgvLog });

        CliResult process = await repo.RunAsync("run", "code-reviewer", "--brief", "Review the diff below.\n\n## Diff\n+x", "--json", "--no-live");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Contains("--no-session-persistence", Argv());
    }

    [Fact]
    public async Task ANeedsInputClaudeJobIsContinuedByJobsAnswerBecauseItsSessionWasSavedAsync()
    {
        string asked = await AskAsync(0.10m);

        (CliResult process, string next) = await AnswerAsync(asked, new FakeClaudeScript { Summary = "finished", SessionId = Session, TotalCostUsd = 0.30m, ArgvLog = repo.ArgvLog });

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal("success", Str(repo.ReadResult(next), "status"));
        string[] argv = Argv();
        Assert.Equal(Session, argv[Array.IndexOf(argv, "--resume") + 1]);
        Assert.DoesNotContain("--no-session-persistence", argv);
    }

    // What a pre-fix launch looked like: the session was started with the flag (the fake is told so by its own record), so the answer's resume finds nothing,
    // the continuation fails with claude's own words, and the receipt shows it instead of a success that lost the answer.
    [Fact]
    public async Task AJobWhoseSessionWasStartedWithTheFlagCannotBeContinuedAsync()
    {
        string asked = await AskAsync(0.10m);
        File.AppendAllText(Path.Combine(FakeDirectory, "unsaved-sessions"), Session + "\n");

        (CliResult process, string next) = await AnswerAsync(asked, new FakeClaudeScript { Summary = "finished", SessionId = Session });

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        JsonElement receipt = repo.ReadResult(next);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Contains("No conversation found", receipt.GetRawText(), StringComparison.Ordinal);
    }

    // The fake itself: the same session id resumes after a run without the flag and does not after a run with it.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheFakeClaudeHonoursTheFlagOnResumeAsync(bool withFlag, bool resumes)
    {
        repo.Script(new FakeClaudeScript { Summary = "x", SessionId = "fresh-session" });
        await RunFakeAsync(withFlag ? ["-p", "--no-session-persistence"] : ["-p"]);

        (int code, string output) = await RunFakeAsync(["-p", "--resume", "fresh-session"]);

        Assert.Equal(resumes, code == 0);
        Assert.Equal(!resumes, output.Contains("No conversation found with session ID: fresh-session", StringComparison.Ordinal));
    }

    private async Task<(int Code, string Output)> RunFakeAsync(string[] args)
    {
        System.Diagnostics.ProcessStartInfo info = new(repo.ScriptPath) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("fake claude did not start");
        process.StandardInput.Close();
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output);
    }

    // ---- finding 2: a continuation pays only its own cost ------------------------------------------------------------------------

    // First job 0.10; its answer reports claude's restored running total 0.25 (charged 0.15) and asks again; the next answer reports 0.45 (charged 0.20,
    // not 0.35 as against the first job, nor 0.45 as the whole running total).
    [Fact]
    public async Task AChainOfTwoContinuationsIsChargedEachOnlyWhatItAddedAsync()
    {
        string first = await AskAsync(0.10m);
        Assert.Equal(0.10m, Cost(repo.ReadResult(first)));

        (CliResult secondProcess, string second) = await AnswerAsync(first, new FakeClaudeScript { ReportJson = Ask, SessionId = Session, TotalCostUsd = 0.25m });
        Assert.Equal(ExitCodes.NeedsInput, secondProcess.ExitCode);
        (CliResult thirdProcess, string third) = await AnswerAsync(second, new FakeClaudeScript { Summary = "finished", SessionId = Session, TotalCostUsd = 0.45m });
        Assert.Equal(ExitCodes.Ok, thirdProcess.ExitCode);

        JsonElement secondReceipt = repo.ReadResult(second);
        Assert.Equal(0.15m, Cost(secondReceipt));
        Assert.Equal(0.25m, secondReceipt.GetProperty("raw").GetProperty("total_cost_usd").GetDecimal());
        Assert.Equal(0.20m, Cost(repo.ReadResult(third)));
    }

    [Fact]
    public async Task AContinuationWhoseSessionHasNoReceiptIsChargedItsWholeTotalWithAWarningAsync()
    {
        string first = await AskAsync(0.10m);
        File.Delete(Path.Combine(repo.JobsRoot, first, "result.json"));
        File.WriteAllText(Path.Combine(repo.JobsRoot, first, "result.json"), "{\"job_id\":\"" + first + "\",\"status\":\"needs_input\",\"session_id\":\"" + Session + "\",\"role\":\"builder\",\"backend\":\"claude\"}");

        (_, string next) = await AnswerAsync(first, new FakeClaudeScript { Summary = "finished", SessionId = Session, TotalCostUsd = 0.25m });

        JsonElement receipt = repo.ReadResult(next);
        Assert.Equal(0.25m.ToString(CultureInfo.InvariantCulture), Cost(receipt).ToString(CultureInfo.InvariantCulture));
        Assert.Contains(receipt.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()?.Contains("whole running total", StringComparison.Ordinal) == true);
    }
}
