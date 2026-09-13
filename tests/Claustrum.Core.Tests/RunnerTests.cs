using Claustrum.Core.Backends;
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

    [Fact]
    public async Task NonpositiveTimeoutThrowsBeforeSpawnAsync()
    {
        Runner runner = NewRunner(ScriptedBackend.Success());
        RunRequest request = MakeRequest(timeout: TimeSpan.Zero);

        await Assert.ThrowsAsync<RunRequestException>(() => runner.RunAsync(request, MakeRole(), DefaultOptions(), CancellationToken.None));
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
