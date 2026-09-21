using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// Runner.RunAsync end-to-end with a ScriptedBackend pointed at a real, short-lived OS command
// instead of `claude` (docs brief item 3's sixth bullet). HomeRedirectPlatform keeps
// ~/.claustrum/jobs under a per-test temp dir instead of the real home (JobDirectory.Create reads
// IPlatform.HomeDirectory, never Environment directly).
public sealed class RunnerTests : IDisposable
{
    private readonly string homeDir = Directory.CreateTempSubdirectory("claustrum-home-").FullName;
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cwd-").FullName;

    public void Dispose()
    {
        TryDelete(homeDir);
        TryDelete(cwd);
    }

    [Fact]
    public async Task BlindGateAllowsABriefThatMerelyMentionsTheWordsAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(brief: "## Task\nMention claustrum-report and a blind review in prose.\n## Scope\nfoo\n");

        RunResult result = await runner.RunAsync(request, MakeRole(blind: true), DefaultOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
    }

    [Fact]
    public async Task BlindGateRejectsAPastedReportFenceAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(brief: "Body text.\n```claustrum-report\n{}\n```\n");

        await Assert.ThrowsAsync<BlindGateException>(() => runner.RunAsync(request, MakeRole(blind: true), DefaultOptions(), CancellationToken.None));
    }

    [Theory]
    [InlineData("## Context\nsome rationale")]
    [InlineData("## Plan\nstep 1")]
    [InlineData("## Rationale\nbecause")]
    public async Task BlindGateRejectsRationaleHeadersAsync(string rationaleSection)
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(brief: $"## Task\ndo it\n{rationaleSection}\n");

        await Assert.ThrowsAsync<BlindGateException>(() => runner.RunAsync(request, MakeRole(blind: true), DefaultOptions(), CancellationToken.None));
    }

    // 2026-09-14 review finding #7: JobDirectory.Create (and the Prune it triggers) used to run as an
    // argument, before ValidateTimeout/EnsureBlindGate — a rejection left an empty job dir behind and
    // had already pruned history. Post-fix, a blind-gate rejection must create no job directory at all.
    [Fact]
    public async Task BlindGateRejectionCreatesNoJobDirectoryAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(brief: "Body text.\n```claustrum-report\n{}\n```\n");
        string jobsRoot = Path.Combine(homeDir, ".claustrum", "jobs");

        await Assert.ThrowsAsync<BlindGateException>(() => runner.RunAsync(request, MakeRole(blind: true), DefaultOptions(), CancellationToken.None));

        Assert.False(Directory.Exists(jobsRoot));
    }

    [Fact]
    public async Task NonpositiveTimeoutThrowsBeforeSpawnAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(timeout: TimeSpan.Zero);

        await Assert.ThrowsAsync<RunRequestException>(() => runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None));
    }

    // Finding #7 again, for the *first* of the two checks that used to run after JobDirectory.Create:
    // ValidateTimeout rejects before the blind gate, so a rejected timeout must leak no job dir either.
    [Fact]
    public async Task NonpositiveTimeoutRejectionCreatesNoJobDirectoryAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(timeout: TimeSpan.Zero);
        string jobsRoot = Path.Combine(homeDir, ".claustrum", "jobs");

        await Assert.ThrowsAsync<RunRequestException>(() => runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None));

        Assert.False(Directory.Exists(jobsRoot));
    }

    // The 5-arg overload finding #7's fix introduced (`jobOverride`) is what MCP delegate_async needs
    // to know the job id before the run finishes; nothing in Core exercised it directly. The run must
    // land in the caller's own directory and create no second one.
    [Fact]
    public async Task PrecreatedJobPathsOverloadUsesTheCallersJobDirectoryAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        Runner runner = new(platform, new BackendRegistry([ScriptedBackend.Success()]), new ProcessRunner(platform));
        JobPaths job = JobDirectory.Create(platform);

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), DefaultOptions(), job, CancellationToken.None);

        Assert.Equal(job.Id, result.JobId);
        Assert.True(File.Exists(job.ResultJson));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(homeDir, ".claustrum", "jobs")));
    }

    // Same overload, validation half: a pre-created job directory does not license skipping the gate.
    [Fact]
    public async Task PrecreatedJobPathsOverloadStillEnforcesTheBlindGateAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        Runner runner = new(platform, new BackendRegistry([ScriptedBackend.Success()]), new ProcessRunner(platform));
        JobPaths job = JobDirectory.Create(platform);
        RunRequest request = MakeRequest(brief: "## Task\ndo it\n## Rationale\nbecause\n");

        await Assert.ThrowsAsync<BlindGateException>(() => runner.RunAsync(request, MakeRole(blind: true), DefaultOptions(), job, CancellationToken.None));

        Assert.False(File.Exists(job.ResultJson));
    }

    [Fact]
    public async Task MissingAttachmentFileThrowsBeforeSpawnAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(attachFiles: [Path.Combine(cwd, "does-not-exist.txt")]);

        await Assert.ThrowsAsync<RunRequestException>(() => runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task ExistingAttachmentIsAppendedToTheBriefAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        string attachPath = Path.Combine(cwd, "notes.txt");
        File.WriteAllText(attachPath, "extra context");
        RunRequest request = MakeRequest(brief: "do it", attachFiles: [attachPath]);

        await runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None);

        Assert.NotNull(backend.LastRun);
        Assert.Contains($"## Attached: {attachPath}", backend.LastRun!.Brief, StringComparison.Ordinal);
        Assert.Contains("extra context", backend.LastRun.Brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleWithReportSchemaGetsTheReportTrailerAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        RunRequest request = MakeRequest(brief: "do it");

        await runner.RunAsync(request, MakeRole(hasReport: true), DefaultOptions(), CancellationToken.None);

        Assert.NotNull(backend.LastRun);
        Assert.Contains("```claustrum-report fenced JSON block from your system prompt", backend.LastRun!.Brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleWithoutReportSchemaGetsNoTrailerAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        RunRequest request = MakeRequest(brief: "do it");

        await runner.RunAsync(request, MakeRole(hasReport: false), DefaultOptions(), CancellationToken.None);

        Assert.NotNull(backend.LastRun);
        Assert.Equal("do it", backend.LastRun!.Brief);
    }

    [Fact]
    public async Task TimeoutStillWritesAResultJsonAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Sleep(10));
        RunRequest request = MakeRequest(timeout: TimeSpan.FromMilliseconds(300));

        RunResult result = await runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.True(File.Exists(ResultJsonPath(result)));
    }

    [Fact]
    public async Task MidrunCancelStillWritesAResultJsonAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Sleep(10));
        RunRequest request = MakeRequest();
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        RunResult result = await runner.RunAsync(request, MakeRole(), DefaultOptions(), cts.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.True(File.Exists(ResultJsonPath(result)));
    }

    [Fact]
    public async Task UnregisteredBackendShortCircuitsWithoutSpawningAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest();

        RunResult result = await runner.RunAsync(request, MakeRole(backend: "does-not-exist"), DefaultOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Equal(ReportStatus.Missing, result.ReportStatus);
    }

    // Confirms the fix for the `BackendNotFoundException` that used to escape RunCoreAsync
    // uncaught (NOTES.md "Runner always yields a result after the process ran", 2026-09-14 update):
    // a registered backend resolving to no binary on PATH now yields the same BackendMissing
    // status as the unregistered-name case below, not a thrown exception.
    [Fact]
    public async Task BackendNotOnPathYieldsStructuredBackendMissingResultAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.NotOnPath());
        RunRequest request = MakeRequest();

        RunResult result = await runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.Equal(ReportStatus.Missing, result.ReportStatus);
        Assert.Contains("was not found on PATH", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(ResultJsonPath(result)));
    }

    // docs/PLAN.md §D4 / NOTES.md "Tree budget accounting is a file ledger": RunOptions.Tree routes
    // a run through BudgetLedger.AdmitAsync before anything is written or spawned.
    [Fact]
    public async Task AdmittedRunClosesTheEntryWithTheReportedCostAsync()
    {
        ScriptedBackend backend = ScriptedWithCost(0.30m);
        Runner runner = NewRunner(backend);
        JobTreeBudget tree = new("tree-a", 1.00m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.30m, result.CostUsd);
        decimal remaining = await BudgetLedger.PeekRemainingAsync(new HomeRedirectPlatform(homeDir), tree);
        Assert.Equal(0.70m, remaining);
    }

    [Fact]
    public async Task SecondRunInATreeSeesTheReducedRemainderAsync()
    {
        JobTreeBudget tree = new("tree-remainder", 1.00m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult first = await NewRunner(ScriptedWithCost(0.40m)).RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);
        Assert.Equal(RunStatus.Success, first.Status);
        RunResult second = await NewRunner(ScriptedWithCost(0.10m)).RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.Success, second.Status);
        decimal remaining = await BudgetLedger.PeekRemainingAsync(new HomeRedirectPlatform(homeDir), tree);
        Assert.Equal(0.50m, remaining);
    }

    [Fact]
    public async Task ExhaustedTreeRefusesWithoutSpawningTheBackendAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        JobTreeBudget tree = new("tree-exhausted", 0.00m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Equal(-1, result.ExitCode);
        Assert.NotNull(result.Error);
        Assert.Null(backend.LastRun);
        string jobDirectory = Path.GetDirectoryName(ResultJsonPath(result))!;
        Assert.True(File.Exists(ResultJsonPath(result)));
        Assert.False(File.Exists(Path.Combine(jobDirectory, "system.md")));
        Assert.False(File.Exists(Path.Combine(jobDirectory, "request.json")));
    }

    [Fact]
    public async Task NullRequestedCapIsClampedToTheRemainderAndPersistedAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        JobTreeBudget tree = new("tree-clamp", 0.75m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.75m, backend.LastRun!.BudgetUsd);
        string requestJson = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(ResultJsonPath(result))!, "request.json"), CancellationToken.None);
        Assert.Contains("\"budget_usd\":0.75", requestJson, StringComparison.Ordinal);
    }

    // Cursor/copilot report no cost at all (NOTES.md "Cost-less backends are charged their cap"):
    // the tree cap must still be a real charge, with a warning since cost_usd stays null.
    [Fact]
    public async Task CostlessBackendThatRanIsChargedTheGrantedCapWithAWarningAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        JobTreeBudget tree = new("tree-costless", 0.50m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.CostUsd);
        Assert.Contains("cost not reported by backend 'scripted'; charged the granted cap $0.50 to tree 'tree-costless'", result.Warnings);
        decimal remaining = await BudgetLedger.PeekRemainingAsync(new HomeRedirectPlatform(homeDir), tree);
        Assert.Equal(0m, remaining);
    }

    [Fact]
    public async Task BackendMissingInsideATreeChargesZeroAndLeavesTheRemainderIntactAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.NotOnPath());
        JobTreeBudget tree = new("tree-missing-backend", 1.00m);
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        decimal remaining = await BudgetLedger.PeekRemainingAsync(new HomeRedirectPlatform(homeDir), tree);
        Assert.Equal(1.00m, remaining);
    }

    // Symmetric to CompleteAsync's own error handling: a ledger AdmitAsync cannot reach leaves
    // through the same Failed funnel as any other pre-spawn failure, not as a throw.
    [Fact]
    public async Task LedgerErrorAtAdmissionYieldsFailedWithoutSpawningAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        JobTreeBudget tree = new("tree-broken-ledger", 1.00m);
        string budgetDirectory = Path.Combine(homeDir, ".claustrum", "budget");
        Directory.CreateDirectory(budgetDirectory);
        File.WriteAllText(Path.Combine(budgetDirectory, "tree-broken-ledger"), "blocks Directory.CreateDirectory below it");
        RunOptions options = DefaultOptions() with { Tree = tree };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Null(backend.LastRun);
        Assert.True(File.Exists(ResultJsonPath(result)));
    }

    [Fact]
    public async Task PreMadeRefusedAdmissionShortCircuitsWithoutReAdmittingAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = NewRunner(backend);
        BudgetAdmission refusal = new(Admitted: false, Spent: 1.00m, Reserved: 0m, Remaining: 0m, EffectiveCap: null, Reason: "pre-refused for the test", Reservation: null);
        RunOptions options = DefaultOptions() with { Admission = refusal };

        RunResult result = await runner.RunAsync(MakeRequest(), MakeRole(), options, CancellationToken.None);

        Assert.Equal(RunStatus.BudgetExceeded, result.Status);
        Assert.Equal("pre-refused for the test", result.Error);
        Assert.Null(backend.LastRun);
        Assert.False(Directory.Exists(Path.Combine(homeDir, ".claustrum", "budget")));
    }

    [Fact]
    public async Task NoTreeMeansNoBudgetDirectoryAtAllAsync()
    {
        RunResult result = await NewRunner(ScriptedBackend.Success()).RunAsync(MakeRequest(), MakeRole(), DefaultOptions(), CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.False(Directory.Exists(Path.Combine(homeDir, ".claustrum", "budget")));
    }

    private static ScriptedBackend ScriptedWithCost(decimal cost) => OperatingSystem.IsWindows()
        ? new ScriptedBackend("cmd", ["/c", "exit 0"], new ParsedOutput("ok", null, cost, null, [], null, false))
        : new ScriptedBackend("sh", ["-c", "exit 0"], new ParsedOutput("ok", null, cost, null, [], null, false));

    private Runner NewRunner(IBackend backend)
    {
        HomeRedirectPlatform platform = new(homeDir);
        BackendRegistry registry = new([backend]);
        return new Runner(platform, registry, new ProcessRunner(platform));
    }

    private RunRequest MakeRequest(string? brief = "do it", string[]? attachFiles = null, TimeSpan? timeout = null, string? resume = null) => new(
        Role: "builder", Brief: brief, BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
        Permission: null, BudgetUsd: null, Timeout: timeout, ResumeSession: resume,
        AttachFiles: attachFiles ?? [], Env: [], Stream: false);

    private static ResolvedRole MakeRole(bool blind = false, string backend = "scripted", bool hasReport = false) =>
        new("builder", "system prompt", backend, "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), blind, hasReport);

    private static RunOptions DefaultOptions() => new(DiffByteCapBytes: 200_000);

    private static string ResultJsonPath(RunResult result) => Path.Combine(Path.GetDirectoryName(result.LogPath)!, "result.json");

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
