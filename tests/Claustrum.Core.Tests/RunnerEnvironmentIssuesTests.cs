using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Tests;

// #145 through Runner: what the backend's trace and the role's report say about the environment lands in the receipt's environment_issues, after
// the issues the caller found at dispatch (RunOptions.PreRunIssues, first: dispatch); a `success` with any issue is `success_with_issues`, every
// other status keeps its value and still lists them; a check that throws costs a warning and never the run's result. The trace here is
// handed in by the test; the log-to-trace readers are tested with the classifier (EnvironmentClassifier*Tests).
public sealed class RunnerEnvironmentIssuesTests : IDisposable
{
    private static readonly EnvironmentIssue dispatchIssue = new(EnvironmentIssueKind.MissingDirectory, "a configured read_dirs directory does not exist", "/gone", 1, "dispatch");

    private readonly string homeDir = Directory.CreateTempSubdirectory("claustrum-home-").FullName;
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cwd-").FullName;

    public void Dispose()
    {
        Directory.Delete(homeDir, recursive: true);
        Directory.Delete(cwd, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParsedOutput Said(string finalMessage) => new(finalMessage, null, null, null, [], null, false);

    private static string Report(string json) => $"done\n\n```claustrum-report\n{json}\n```";

    private static ScriptedBackend Shell(ParsedOutput? parsed = null, string unix = "exit 0", string windows = "exit 0") => ScriptedBackend.Shell(unix, windows, parsed);

    private static RunTrace RefusedRead(string path) =>
        new([new TracedCall("read", ToolFamily.Read, null, path, null, true, "Permission denied: external_directory", null, 7)], []);

    private static ResolvedRole Role(bool blind = false) =>
        new("builder", "system prompt", "scripted", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), blind, HasReport: true);

    private Runner NewRunner(IBackend backend)
    {
        HomeRedirectPlatform platform = new(homeDir);
        return new Runner(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
    }

    private RunRequest Request() => new(
        Role: "builder", Brief: "do it", BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
        Permission: null, BudgetUsd: null, Timeout: null, ResumeSession: null, AttachFiles: [], Env: [], Stream: false);

    private static RunOptions Options(EnvironmentIssue[]? preRun = null, string[]? readDirs = null) =>
        new(DiffByteCapBytes: 200_000, ReadDirs: readDirs, PreRunIssues: preRun);

    private Task<RunResult> RunAsync(IBackend backend, RunOptions? options = null) => NewRunner(backend).RunAsync(Request(), Role(), options ?? Options(), Ct);

    private static JsonElement OnDisk(RunResult result)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.LogPath)!, "result.json")));
        return document.RootElement.Clone();
    }

    // ---- a clean run is untouched --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACleanRunStaysSuccessWithNoEnvironmentIssuesOnTheResultOrOnDiskAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(Shell(), _ => new RunTrace([], [])));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.EnvironmentIssues);
        JsonElement onDisk = OnDisk(result);
        Assert.Equal("success", onDisk.GetProperty("status").GetString());
        Assert.True(!onDisk.TryGetProperty("environment_issues", out JsonElement issues) || issues.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ABackendWithNoTraceStillClassifiesTheReportAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(Shell(Said(Report(/*lang=json,strict*/ """{"status":"blocked","blocked_by":["no docker socket"]}"""))), _ => null));

        Assert.Equal(RunStatus.SuccessWithIssues, result.Status);
        EnvironmentIssue issue = Assert.Single(result.EnvironmentIssues!);
        Assert.Equal(EnvironmentIssueKind.ReportedBlocker, issue.Kind);
        Assert.Equal("report", issue.First);
    }

    // ---- success becomes success_with_issues -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATraceIssueTurnsSuccessIntoSuccessWithIssuesAndItIsOnTheResultJsonAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(Shell(), _ => RefusedRead("/home/me/repos/odoo/shared")));

        Assert.Equal(RunStatus.SuccessWithIssues, result.Status);
        EnvironmentIssue issue = Assert.Single(result.EnvironmentIssues!);
        Assert.Equal(EnvironmentIssueKind.ExternalDirectory, issue.Kind);
        Assert.Equal("/home/me/repos/odoo/shared", issue.Where);
        Assert.Equal("stdout.log:7", issue.First);

        JsonElement onDisk = OnDisk(result);
        Assert.Equal("success_with_issues", onDisk.GetProperty("status").GetString());
        JsonElement written = onDisk.GetProperty("environment_issues")[0];
        Assert.Equal("external_directory", written.GetProperty("kind").GetString());
        Assert.Equal("Permission denied: external_directory", written.GetProperty("what").GetString());
        Assert.Equal("/home/me/repos/odoo/shared", written.GetProperty("where").GetString());
        Assert.Equal(1, written.GetProperty("count").GetInt32());
        Assert.Equal("stdout.log:7", written.GetProperty("first").GetString());
    }

    [Fact]
    public async Task APreRunIssueAloneMakesACleanRunSuccessWithIssuesAndTheIssueCarriesFirstDispatchAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(Shell(), _ => new RunTrace([], [])), Options(preRun: [dispatchIssue]));

        Assert.Equal(RunStatus.SuccessWithIssues, result.Status);
        Assert.Equal([dispatchIssue], result.EnvironmentIssues!);
        Assert.Equal("dispatch", OnDisk(result).GetProperty("environment_issues")[0].GetProperty("first").GetString());
    }

    [Fact]
    public async Task DispatchIssuesComeFirstThenTheTraceThenTheReportAsync()
    {
        TracedBackend backend = new(Shell(Said(Report(/*lang=json,strict*/ """{"status":"partial","blocked_by":["no network"]}"""))), _ => RefusedRead("/srv/other"));

        RunResult result = await RunAsync(backend, Options(preRun: [dispatchIssue]));

        Assert.Equal(
            [EnvironmentIssueKind.MissingDirectory, EnvironmentIssueKind.ExternalDirectory, EnvironmentIssueKind.ReportedBlocker],
            result.EnvironmentIssues!.Select(issue => issue.Kind));
        Assert.Equal(["dispatch", "stdout.log:7", "report"], result.EnvironmentIssues!.Select(issue => issue.First));
    }

    // ---- every other status keeps its value and lists its issues ------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedRunStaysFailedAndStillListsItsIssuesAsync()
    {
        ScriptedBackend failing = ScriptedBackend.Shell("exit 3", "exit 3");

        RunResult result = await RunAsync(new TracedBackend(failing, _ => RefusedRead("/srv/other")), Options(preRun: [dispatchIssue]));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal([EnvironmentIssueKind.MissingDirectory, EnvironmentIssueKind.ExternalDirectory], result.EnvironmentIssues!.Select(issue => issue.Kind));
        Assert.Equal("failed", OnDisk(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ANeedsInputRunStaysNeedsInputAndStillListsItsIssuesAsync()
    {
        string report = Report(/*lang=json,strict*/ """{"status":"needs_input","questions":[{"key":"q1","prompt":"which branch?"}],"blocked_by":["no access to /srv/other"]}""");

        RunResult result = await RunAsync(new TracedBackend(Shell(Said(report)), _ => new RunTrace([], [])));

        Assert.Equal(RunStatus.NeedsInput, result.Status);
        EnvironmentIssue issue = Assert.Single(result.EnvironmentIssues!);
        Assert.Equal("no access to /srv/other", issue.What);
    }

    [Fact]
    public async Task ACancelledRunStaysCancelledAndTheDispatchIssuesAreStillOnItsReceiptAsync()
    {
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        RunResult result = await NewRunner(new TracedBackend(ScriptedBackend.Sleep(10), _ => null)).RunAsync(Request(), Role(), Options(preRun: [dispatchIssue]), cancel.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal([dispatchIssue], result.EnvironmentIssues!);
    }

    [Fact]
    public async Task ABackendMissingReceiptCarriesTheDispatchIssuesTooAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(ScriptedBackend.NotOnPath(), _ => null), Options(preRun: [dispatchIssue]));

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Equal([dispatchIssue], result.EnvironmentIssues!);
        Assert.Equal("dispatch", OnDisk(result).GetProperty("environment_issues")[0].GetProperty("first").GetString());
    }

    [Fact]
    public async Task ABudgetRefusalAndAnUnregisteredBackendCarryTheDispatchIssuesTooAsync()
    {
        ScriptedBackend inner = ScriptedBackend.Success();
        RunOptions exhausted = Options(preRun: [dispatchIssue]) with { Tree = new JobTreeBudget("tree-env-issues", 0.00m) };

        RunResult budget = await RunAsync(new TracedBackend(inner, _ => null), exhausted);
        RunResult unregistered = await NewRunner(new TracedBackend(inner, _ => null)).RunAsync(Request(), Role() with { Backend = "does-not-exist" }, Options(preRun: [dispatchIssue]), Ct);

        Assert.Equal(RunStatus.BudgetExceeded, budget.Status);
        Assert.Equal([dispatchIssue], budget.EnvironmentIssues!);
        Assert.Equal(RunStatus.BackendMissing, unregistered.Status);
        Assert.Equal([dispatchIssue], unregistered.EnvironmentIssues!);
        Assert.Null(inner.LastRun);
    }

    // ---- RefuseAsync: a refusal the caller decided before Runner was entered -----------------------------------------------------------------------

    [Fact]
    public async Task ARefuseAsyncReceiptCarriesThePreRunIssuesAndKeepsItsStatusAsync()
    {
        JobPaths job = JobDirectory.Create(new HomeRedirectPlatform(homeDir));

        RunResult result = await Runner.RefuseAsync(job, Role(), RunStatus.Failed, "--branch nosuch: no local branch of that name", preRunIssues: [dispatchIssue]);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal([dispatchIssue], result.EnvironmentIssues!);
        JsonElement onDisk = OnDisk(result);
        Assert.Equal("failed", onDisk.GetProperty("status").GetString());
        Assert.Equal("a configured read_dirs directory does not exist", onDisk.GetProperty("environment_issues")[0].GetProperty("what").GetString());
    }

    [Fact]
    public async Task ARefuseAsyncWithNoIssuesLeavesTheFieldOffTheReceiptAsync()
    {
        JobPaths job = JobDirectory.Create(new HomeRedirectPlatform(homeDir));

        RunResult none = await Runner.RefuseAsync(job, Role(), RunStatus.Failed, "refused", preRunIssues: []);

        Assert.Null(none.EnvironmentIssues);
    }

    // ---- a check that throws is a warning, never a lost result ------------------------------------------------------------------------------------

    [Fact]
    public async Task ATraceThatThrowsIsAWarningAndTheReportsIssuesAreStillListedAsync()
    {
        TracedBackend backend = new(Shell(Said(Report(/*lang=json,strict*/ """{"status":"done","blocked_by":["no network"]}"""))), _ => throw new InvalidOperationException("boom"));

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.SuccessWithIssues, result.Status);
        Assert.Equal("no network", Assert.Single(result.EnvironmentIssues!).What);
        string warning = Assert.Single(result.Warnings!);
        Assert.Equal("environment check: the scripted trace could not be read (InvalidOperationException: boom); environment_issues may be incomplete", warning);
    }

    [Fact]
    public async Task AClassifierThatThrowsIsAWarningAndTheRunStaysSuccessAsync()
    {
        TracedBackend backend = new(Shell(), _ => new RunTrace(null!, []));

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.EnvironmentIssues);
        string warning = Assert.Single(result.Warnings!);
        Assert.StartsWith("environment check failed (NullReferenceException: ", warning, StringComparison.Ordinal);
        Assert.EndsWith("environment_issues may be incomplete", warning, StringComparison.Ordinal);
        Assert.Equal("success", OnDisk(result).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AFailedCheckDoesNotDropTheDispatchIssuesAsync()
    {
        RunResult result = await RunAsync(new TracedBackend(Shell(), _ => new RunTrace(null!, [])), Options(preRun: [dispatchIssue]));

        Assert.Equal(RunStatus.SuccessWithIssues, result.Status);
        Assert.Equal([dispatchIssue], result.EnvironmentIssues!);
        Assert.Single(result.Warnings!);
    }

    // ---- read dirs reach the backend ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheOptionsReadDirsAreHandedToTheBackendInTheResolvedRunAsync()
    {
        TracedBackend backend = new(Shell(), _ => null);

        await RunAsync(backend, Options(readDirs: ["/srv/shared", "/srv/devkit/.git"]));

        Assert.Equal(["/srv/shared", "/srv/devkit/.git"], backend.LastRun!.ReadDirs!);
    }

    [Fact]
    public async Task WithNoReadDirsTheBackendGetsNoneAsync()
    {
        TracedBackend backend = new(Shell(), _ => null);

        await RunAsync(backend);

        Assert.Null(backend.LastRun!.ReadDirs);
    }
}
