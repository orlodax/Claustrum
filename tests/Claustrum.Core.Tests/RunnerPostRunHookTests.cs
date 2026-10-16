using System.Collections.Concurrent;
using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// #125 and #123 at Runner level: a backend that is also an IPostRunHook (opencode's `session export`) is asked for a baseline
// before the process starts and for the real cost after Parse, and whatever it does it cannot change the run's status; a cancelled
// job is not asked at all. Also what Runner records for `jobs follow` (backend.json) and how it hands a backend without a stream
// formatter the lines. The scripted "export" is a delegate, so nothing here needs opencode.
public sealed class RunnerPostRunHookTests : IDisposable
{
    private readonly string homeDir = Directory.CreateTempSubdirectory("claustrum-home-").FullName;
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cwd-").FullName;
    private readonly CancellationTokenSource externalCancel = new();

    public void Dispose()
    {
        externalCancel.Dispose();
        TryDelete(homeDir);
        TryDelete(cwd);
    }

    private static ParsedOutput Parsed(string? session = "ses_1", decimal? cost = null, bool isError = false) =>
        new("ok", session, cost, null, [], null, isError);

    private static ScriptedBackend Exit(int code, ParsedOutput parsed) =>
        ScriptedBackend.Shell($"exit {code}", $"exit {code}", parsed);

    private async Task<RunResult> RunAsync(IBackend backend, string? resume = null, Action<string>? onStreamLine = null, bool streamRaw = false, TimeSpan? cancelAfter = null)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, externalCancel.Token);
        if (cancelAfter is { } delay)
            linked.CancelAfter(delay);

        HomeRedirectPlatform platform = new(homeDir);
        Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
        RunRequest request = new(
            Role: "builder", Brief: "do it", BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
            Permission: null, BudgetUsd: null, Timeout: null, ResumeSession: resume,
            AttachFiles: [], Env: [], Stream: onStreamLine is not null);
        ResolvedRole role = new("builder", "system prompt", "scripted", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), Blind: false, HasReport: false);

        return await runner.RunAsync(request, role, new RunOptions(DiffByteCapBytes: 200_000, OnStreamLine: onStreamLine, StreamRaw: streamRaw), linked.Token);
    }

    [Fact]
    public async Task TheHooksCostUsageAndWarningFlowIntoTheReceiptAsync()
    {
        Usage usage = new(10, 20, 30, 40);
        HookedBackend backend = new(Exit(0, Parsed()), postRun: (_, _, _) => new PostRunResult(0.25m, usage, "export said so"));

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.25m, result.CostUsd);
        Assert.Equal(usage, result.Usage);
        Assert.Equal("export said so", result.Warnings.First());
        Assert.Equal(1, backend.PostRunCalls);
    }

    [Fact]
    public async Task AHookAnswerOfNullLeavesTheCostTheStreamGaveAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed(cost: 0.1m)), postRun: (_, _, _) => null);

        RunResult result = await RunAsync(backend);

        Assert.Equal(0.1m, result.CostUsd);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task AWarningOnlyAnswerKeepsTheCostAndAddsTheWarningAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed(cost: 0.1m)), postRun: (_, _, _) => new PostRunResult(null, null, "export failed: cost from step_finish events"));

        RunResult result = await RunAsync(backend);

        Assert.Equal(0.1m, result.CostUsd);
        Assert.Equal(["export failed: cost from step_finish events"], result.Warnings);
    }

    // Whatever the hook returns, the status is the run's own: a failed run stays failed with the corrected cost.
    [Fact]
    public async Task TheHookNeverChangesAFailedRunsStatusAsync()
    {
        HookedBackend backend = new(Exit(3, Parsed(isError: true)), postRun: (_, _, _) => new PostRunResult(0.5m, null, null));

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(0.5m, result.CostUsd);
    }

    [Fact]
    public async Task AThrowingHookIsCaughtAndTheRunKeepsItsStatusAndGainsAWarningAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed(cost: 0.1m)), postRun: (_, _, _) => throw new InvalidOperationException("boom"));

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.1m, result.CostUsd);
        Assert.Equal(["post-run cost lookup failed (InvalidOperationException: boom): cost is what the run's own output gave"], result.Warnings);
    }

    [Fact]
    public async Task AThrowingPreRunIsCaughtTooAndANonResumedRunJustHasNoBaselineAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed()), preRun: _ => throw new InvalidOperationException("pre"), postRun: (_, _, _) => null);

        RunResult result = await RunAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(backend.PostRunBaseline);
    }

    [Fact]
    public async Task AThrowingPreRunOnAResumedRunReachesThePostRunAsAFailureNamedByItsTypeAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed()), preRun: _ => throw new InvalidOperationException("pre"), postRun: (_, _, _) => null);

        RunResult result = await RunAsync(backend, resume: "ses_1");

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(new PreRunBaseline(null, null, "InvalidOperationException: pre"), backend.PostRunBaseline);
    }

    [Fact]
    public async Task TheBaselineThePreRunTookIsWhatThePostRunIsGivenAsync()
    {
        PreRunBaseline baseline = new(0.4358m, new Usage(1, 2, 3, 4), Failure: null);
        HookedBackend backend = new(Exit(0, Parsed()), preRun: _ => baseline, postRun: (_, _, _) => null);

        _ = await RunAsync(backend, resume: "ses_1");

        Assert.Equal(baseline, backend.PostRunBaseline);
        Assert.Equal("ses_1", backend.PreRunContext!.ResumeSession);
        Assert.Equal("ses_1", backend.PostRunContext!.ResumeSession);
    }

    [Fact]
    public async Task ThePreRunRunsBeforeTheProcessAndSeesNoOutputWhilePostRunSeesTheWholeOfItAsync()
    {
        string echo = "echo hello-from-the-run";
        HookedBackend backend = new(ScriptedBackend.Shell(echo, echo, Parsed()), postRun: (_, _, _) => null);

        RunResult result = await RunAsync(backend);

        Assert.Equal("", backend.PreRunContext!.Stdout);
        Assert.Contains("hello-from-the-run", backend.PostRunContext!.Stdout, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(result.LogPath), backend.PostRunContext.JobDirectory);
        Assert.Equal(cwd, backend.PostRunContext.Spec.Cwd);
    }

    // #127 finding 3: a cancelled job is enriched too (a stream cut mid-turn has no cost and would be charged its cap), on a 5 s token, not on
    // CancellationToken.None. Before that change a cancelled job was never asked; the old case also passed for the wrong reason (no session id).
    private static ScriptedBackend CancelledSession() =>
        ScriptedBackend.Shell("sleep 30", "ping -n 31 127.0.0.1 > nul", new ParsedOutput("cut", "ses_1", null, null, [], null, false));

    [Fact]
    public async Task ACancelledJobWithASessionIsAskedForItsPostRunAndChargedTheCleanAnswerAsync()
    {
        HookedBackend backend = new(CancelledSession(), postRun: (_, _, _) => new PostRunResult(0.42m, null, null));

        RunResult result = await RunAsync(backend, cancelAfter: TimeSpan.FromMilliseconds(700));

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(1, backend.PostRunCalls);
        Assert.Equal(0.42m, result.CostUsd);
    }

    // The hook says "I fell back" with a warning; a cancelled job keeps what its own output gave and gets no new warning.
    [Fact]
    public async Task ACancelledJobDiscardsAHookAnswerThatCarriesAWarningAsync()
    {
        HookedBackend backend = new(CancelledSession(), postRun: (_, _, _) => new PostRunResult(9m, null, "export failed: fell back"));

        RunResult result = await RunAsync(backend, cancelAfter: TimeSpan.FromMilliseconds(700));

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(1, backend.PostRunCalls);
        Assert.Null(result.CostUsd);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("export failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledJobsPostRunTokenIsBoundedAtAboutFiveSecondsAsync()
    {
        TimeSpan? waited = null;
        HookedBackend backend = new(CancelledSession(), postRun: (_, _, _) => null, asyncPostRun: async (_, _, _, token) =>
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMinutes(1), token).ContinueWith(_ => { }, TaskScheduler.Default);
            waited = clock.Elapsed;
            return null;
        });

        RunResult result = await RunAsync(backend, cancelAfter: TimeSpan.FromMilliseconds(700));

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.InRange(waited?.TotalSeconds ?? -1, 4.0, 12.0);
    }

    // The baseline export swallows its own cancellation, so Runner has to look again before it starts the process (review of #127).
    [Fact]
    public async Task ACancelDuringThePreRunExportEndsCancelledAndNeverStartsTheBackendAsync()
    {
        string marker = Path.Combine(cwd, "started.txt");
        HookedBackend backend = new(
            ScriptedBackend.Shell("echo started > started.txt", "echo started > started.txt", Parsed()),
            preRun: _ =>
            {
                externalCancel.Cancel();
                return null;
            },
            postRun: (_, _, _) => new PostRunResult(9m, null, "must not appear"));

        RunResult result = await RunAsync(backend, resume: "ses_1");

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(1, backend.PreRunCalls);
        Assert.Equal(0, backend.PostRunCalls);
        Assert.False(File.Exists(marker), "the backend's process must not start on a cancelled run");
        Assert.NotEqual(9m, result.CostUsd);
    }

    [Fact]
    public async Task ARunThatReportedNoSessionIsNotAskedEitherAsync()
    {
        HookedBackend backend = new(Exit(0, Parsed(session: null)), postRun: (_, _, _) => new PostRunResult(9m, null, null));

        RunResult result = await RunAsync(backend);

        Assert.Equal(0, backend.PostRunCalls);
        Assert.Null(result.CostUsd);
    }

    // ---- what `jobs follow` reads while the job runs (#123) ----------------------------------------------------------

    [Fact]
    public async Task BackendJsonNamesTheBackendAndTheModelOfTheRunAsync()
    {
        RunResult result = await RunAsync(ScriptedBackend.Success());

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.LogPath)!, "backend.json")));
        Assert.Equal("scripted", document.RootElement.GetProperty("backend").GetString());
        Assert.Equal("sonnet", document.RootElement.GetProperty("model").GetString());
    }

    // A backend with no formatter is a pass-through: the callback gets the raw lines, with or without --stream raw.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABackendWithoutAFormatterEchoesTheRawLinesInOrderAsync(bool streamRaw)
    {
        // The lines come from a file in the job's cwd: .NET escapes a quote in a cmd /c argument as \", and cmd echoes the backslash (CI, 2026-10-15).
        await File.WriteAllTextAsync(Path.Combine(cwd, "lines.txt"), "one\n{\"two\":2}\n", TestContext.Current.CancellationToken);
        ConcurrentQueue<string> echoed = [];

        RunResult result = await RunAsync(ScriptedBackend.Shell("cat lines.txt", "type lines.txt"), onStreamLine: echoed.Enqueue, streamRaw: streamRaw);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(["one", /*lang=json,strict*/ """{"two":2}"""], echoed);
    }

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
