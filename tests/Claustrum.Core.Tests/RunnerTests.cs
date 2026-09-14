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
