using System.Diagnostics;
using System.Globalization;
using System.Text;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #125: opencode never emits a step_finish for its final message, so the run's real cost is `opencode session export`'s
// `info.cost` - the session's running total, which is why a resumed session is charged after - before (F5: job
// 20261015-163517-22930bff would have been billed $0.5290 against its own $0.0932). The export is a stand-in `opencode`
// script (BackendConfig.Path), so the hook runs its real process path; POSIX shells only, as the script is `sh`.
public sealed class OpencodePostRunTests : IDisposable
{
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-opencode-postrun-").FullName;

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode", name));

    private static void RequirePosixShell()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the stand-in `opencode` is a POSIX shell script.");
    }

    // ---- the stand-in `opencode` -----------------------------------------------------------------------------

    private sealed record Reply(string Stdout, string Stderr = "", int ExitCode = 0);

    private string Count => Path.Combine(scratch, "count");

    private int Calls => File.Exists(Count) ? int.Parse(File.ReadAllText(Count), CultureInfo.InvariantCulture) : 0;

    private string[] Argv => File.Exists(Path.Combine(scratch, "argv.txt")) ? File.ReadAllLines(Path.Combine(scratch, "argv.txt")) : [];

    /// <summary>An `opencode` that answers its first call with the first reply, its second with the second, and every later one with the last.</summary>
    private OpencodeBackend BackendAnswering(params Reply[] replies)
    {
        RequirePosixShell();
        for (int i = 0; i < replies.Length; i++)
        {
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.out"), replies[i].Stdout);
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.err"), replies[i].Stderr);
            File.WriteAllText(Path.Combine(scratch, $"r{i + 1}.exit"), replies[i].ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        string script = new StringBuilder("#!/bin/sh\n")
            .Append($"n=$(cat '{Count}' 2>/dev/null || echo 0)\nn=$((n+1))\nprintf '%s' \"$n\" > '{Count}'\n")
            .Append($"printf '%s\\n' \"$*\" >> '{Path.Combine(scratch, "argv.txt")}'\n")
            .Append($"k=$n\n[ -f '{scratch}/r'$k.out ] || k={replies.Length}\n")
            .Append($"cat '{scratch}/r'$k.out\ncat '{scratch}/r'$k.err >&2\nexit $(cat '{scratch}/r'$k.exit)\n")
            .ToString();
        string path = Path.Combine(scratch, "opencode");
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new OpencodeBackend(new RealPlatform());
    }

    private PostRunContext Context(string? resume = null, string stdout = "") => new(
        new ProcessSpec("opencode", [], scratch, [], []),
        resume,
        scratch,
        new BackendConfig(Path.Combine(scratch, "opencode")),
        EnvPassthroughAll: false,
        stdout);

    private static ParsedOutput Parsed(string? session = "ses_new", decimal? cost = null) => new("ok", session, cost, null, [], null, false);

    private static string Export(decimal cost, int input, int output, int cacheRead, int cacheWrite) =>
        "{\"info\":{\"cost\":" + cost.ToString(CultureInfo.InvariantCulture)
        + ",\"tokens\":{\"input\":" + input + ",\"output\":" + output + ",\"reasoning\":7,\"cache\":{\"read\":" + cacheRead + ",\"write\":" + cacheWrite + "}}},\"messages\":[]}";

    // ---- the figures --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshSessionIsChargedTheExportsInfoCostAndTokensAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0.0932m, result.CostUsd);
        Assert.Equal(new Usage(1200, 340, 5000, 100), result.Usage);
        Assert.Null(result.Warning);
        Assert.Equal(["session export --standalone ses_new"], Argv);
    }

    [Fact]
    public async Task NoSessionIdMeansNothingToExportAndNoScriptRunAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));

        PostRunResult? result = await hook.PostRunAsync(Parsed(session: null), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, Calls);
    }

    [Fact]
    public async Task AFreshRunTakesNoBaselineAndRunsNoExportBeforeItAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));

        PreRunBaseline? baseline = await hook.PreRunAsync(Context(resume: null), TestContext.Current.CancellationToken);

        Assert.Null(baseline);
        Assert.Equal(0, Calls);
    }

    // Regression F5: the export is the session's cumulative total.
    [Fact]
    public async Task AResumedSessionIsChargedTheTotalAfterMinusTheTotalBeforeAsync()
    {
        IPostRunHook hook = BackendAnswering(
            new Reply(Export(0.4358m, 9000, 1000, 20000, 500)),
            new Reply(Export(0.5290m, 10200, 1340, 25000, 600)));
        PostRunContext context = Context(resume: "ses_old");

        PreRunBaseline? baseline = await hook.PreRunAsync(context, TestContext.Current.CancellationToken);
        PostRunResult? result = await hook.PostRunAsync(Parsed(session: "ses_old"), context, baseline, TestContext.Current.CancellationToken);

        Assert.Equal(new PreRunBaseline(0.4358m, new Usage(9000, 1000, 20000, 500), Failure: null), baseline);
        Assert.NotNull(result);
        Assert.Equal(0.0932m, result.CostUsd);
        Assert.Equal(new Usage(1200, 340, 5000, 100), result.Usage);
        Assert.Null(result.Warning);
        Assert.Equal(2, Calls);
        Assert.Equal(["session export --standalone ses_old", "session export --standalone ses_old"], Argv);
    }

    [Fact]
    public async Task ABaselineOnlyAppliesWhenTheRunReallyContinuedThatSessionAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Export(0.5290m, 10200, 1340, 25000, 600)));
        PreRunBaseline baseline = new(1.0000m, new Usage(1, 1, 1, 1), Failure: null);

        PostRunResult? result = await hook.PostRunAsync(Parsed(session: "ses_brand_new"), Context(resume: "ses_old"), baseline, TestContext.Current.CancellationToken);

        Assert.Equal(0.5290m, result!.CostUsd);
        Assert.Equal(new Usage(10200, 1340, 25000, 600), result.Usage);
    }

    [Fact]
    public async Task ATotalThatWentDownBetweenTheTwoExportsIsABadReadingNotARefundAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Export(0.4000m, 10, 10, 10, 10)));
        PreRunBaseline baseline = new(0.5000m, new Usage(1, 1, 1, 1), Failure: null);

        PostRunResult? result = await hook.PostRunAsync(Parsed(session: "ses_old", cost: 0.2m), Context(resume: "ses_old"), baseline, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result.CostUsd);
        Assert.Equal("opencode session export failed (the resumed session's total went down between the two exports): cost from step_finish events", result.Warning);
    }

    [Fact]
    public async Task ATokenCountThatWentDownWhileTheCostDidNotIsRefusedTooAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Export(0.6000m, 5, 10, 10, 10)));
        PreRunBaseline baseline = new(0.5000m, new Usage(9, 1, 1, 1), Failure: null);

        PostRunResult? result = await hook.PostRunAsync(Parsed(session: "ses_old"), Context(resume: "ses_old"), baseline, TestContext.Current.CancellationToken);

        Assert.Null(result!.CostUsd);
        Assert.Contains("went down", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportWithoutATokensObjectStillChargesTheCostAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(/*lang=json,strict*/ """{"info":{"cost":0.25},"messages":[]}"""));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(0.25m, result!.CostUsd);
        Assert.Null(result.Usage);
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task TheExportsTemporaryFileIsGoneAfterwardAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));

        _ = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(scratch, "session-export-*"));
    }

    // ---- when the export fails --------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedExportKeepsTheStepFinishCostWhenTheStreamWasCompleteAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom\n", ExitCode: 3));

        PostRunResult? result = await hook.PostRunAsync(Parsed(cost: 0.5m), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(new PostRunResult(null, null, "opencode session export failed (boom): cost from step_finish events"), result);
    }

    [Fact]
    public async Task AFailedExportFallsBackToTheStepSumWhenOnlyTheFinalTextStepLacksItsFinishAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom", ExitCode: 3));
        string stdout = Fixture("success-no-final-step-finish.jsonl");

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(stdout: stdout), baseline: null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0.0010m, result.CostUsd);
        Assert.Equal(new Usage(3000, 110, 20, 10), result.Usage);
        Assert.Equal("opencode session export failed (boom): cost is the step sum, without the final text step", result.Warning);
    }

    [Fact]
    public async Task TheGenuineCaptureWithAToolStepThenAFinalTextStepIsTheSameCaseAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom", ExitCode: 3));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(stdout: Fixture("success.jsonl")), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(0.0004988494m, result!.CostUsd);
        Assert.Contains("step sum", result.Warning, StringComparison.Ordinal);
    }

    // A tool call after the last step_start means that step was cut off mid-way: its cost is unknown, not merely un-finished.
    [Fact]
    public async Task AFailedExportWithALastStepCutOffAfterAToolCallIsCostUnknownAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom", ExitCode: 3));
        string[] lines = Fixture("success-no-final-step-finish.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string toolInLastStep = /*lang=json,strict*/ """{"type":"tool_use","sessionID":"ses_f35a1b513ffeMTNgfaAtjYaIl6","part":{"id":"prt_x","type":"tool","tool":"bash","state":{"status":"completed","input":{"command":"ls"}}}}""";
        string stdout = string.Join('\n', [.. lines[..^1], toolInLastStep]);

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(stdout: stdout), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(new PostRunResult(null, null, "opencode session export failed (boom): cost unknown, charged the cap"), result);
    }

    // A lone text-only step sums to nothing: the old words claimed a step sum that was not there (review of #127, 2026-10-15).
    [Fact]
    public async Task AFailedExportAfterAOneStepTextOnlyRunSaysTheCostIsUnknownNotAStepSumAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom", ExitCode: 3));
        string stdout = """
            {"type":"step_start","sessionID":"ses_one","part":{"id":"prt_1","type":"step-start"}}
            {"type":"text","sessionID":"ses_one","part":{"id":"prt_2","type":"text","text":"just words"}}
            """;

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(stdout: stdout), baseline: null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(result.CostUsd);
        Assert.Equal("opencode session export failed (boom): cost unknown, charged the cap", result.Warning);
    }

    [Fact]
    public async Task AFailedExportWithNoStreamToFallBackOnIsCostUnknownAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "", ExitCode: 7));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(stdout: ""), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(new PostRunResult(null, null, "opencode session export failed (exit code 7): cost unknown, charged the cap"), result);
    }

    [Fact]
    public async Task AFailureReasonQuotesAtMostThreeHundredCharactersOfStderrAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: new string('e', 500), ExitCode: 1));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal($"opencode session export failed ({new string('e', 300)}): cost unknown, charged the cap", result!.Warning);
    }

    [Fact]
    public async Task ACutOffExportIsReadAgainOnceAndThenFailsNamingTheParseErrorAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("""{"info":{"cost":0."""));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, Calls);
        Assert.Null(result!.CostUsd);
        Assert.StartsWith("opencode session export failed (unparseable export JSON: ", result.Warning, StringComparison.Ordinal);
        Assert.EndsWith("): cost unknown, charged the cap", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACutOffExportThatReadsWholeTheSecondTimeSucceedsWithoutAWarningAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("""{"info":{"cost":0."""), new Reply(Fixture("export.json")));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, Calls);
        Assert.Equal(0.0932m, result!.CostUsd);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"messages":[]}""", "export JSON has no 'info' object")]
    [InlineData("""[1,2]""", "export JSON has no 'info' object")]
    [InlineData(/*lang=json,strict*/ """{"info":{"tokens":{}}}""", "export info has no cost")]
    [InlineData(/*lang=json,strict*/ """{"info":{"cost":"0.1"}}""", "export info has no cost")]
    public async Task AnExportOfTheWrongShapeFailsWithoutARetryAsync(string export, string reason)
    {
        IPostRunHook hook = BackendAnswering(new Reply(export));

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal(1, Calls);
        Assert.Equal($"opencode session export failed ({reason}): cost unknown, charged the cap", result!.Warning);
    }

    [Fact]
    public async Task AFailedBaselineIsNamedWhenTheResumedRunsExportComesLaterAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply("", Stderr: "boom", ExitCode: 3), new Reply(Export(0.5m, 1, 1, 1, 1)));
        PostRunContext context = Context(resume: "ses_old");

        PreRunBaseline? baseline = await hook.PreRunAsync(context, TestContext.Current.CancellationToken);
        PostRunResult? result = await hook.PostRunAsync(Parsed(session: "ses_old"), context, baseline, TestContext.Current.CancellationToken);

        Assert.Equal(new PreRunBaseline(null, null, "boom"), baseline);
        Assert.Equal(new PostRunResult(null, null, "opencode session export failed (the pre-run export of the resumed session failed: boom): cost unknown, charged the cap"), result);
        Assert.Equal(1, Calls);
    }

    [Fact]
    public async Task AnOpencodeThatCannotBeFoundIsAFailureReasonNotAThrowAsync()
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux };
        IPostRunHook hook = new OpencodeBackend(platform);

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("BackendNotFoundException", result.Warning, StringComparison.Ordinal);
        Assert.EndsWith("cost unknown, charged the cap", result.Warning, StringComparison.Ordinal);
    }

    // The reads share the export's deadline (review of #127): a grandchild that inherited the stderr pipe (an MCP/LSP server from the run's
    // config) outlives the export, and a bare ReadToEnd waited for it. Here the grandchild holds the pipe for 5 s; the caller's
    // cancellation, which the reads now honour, ends the export in about a second.
    [Fact]
    public async Task AGrandchildHoldingTheExportsPipeDoesNotHoldTheExportPastItsDeadlineAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));
        string path = Path.Combine(scratch, "opencode");
        File.WriteAllText(path, File.ReadAllText(path).Replace("#!/bin/sh\n", "#!/bin/sh\nsleep 5 >/dev/null </dev/null &\n", StringComparison.Ordinal));
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(1));
        Stopwatch watch = Stopwatch.StartNew();

        PostRunResult? result = await hook.PostRunAsync(Parsed(), Context(), baseline: null, cancel.Token);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"the export waited {watch.Elapsed.TotalSeconds:F1}s for a grandchild's pipe");
        Assert.NotNull(result);
        Assert.Contains("cancelled", result.Warning, StringComparison.Ordinal);
        Assert.Null(result.CostUsd);
    }

    // The two exports are the session's own: the run's env (and `--env` entries) reach the process, and nothing else does.
    [Fact]
    public async Task TheExportRunsWithTheRunsEnvironmentAndInTheRunsWorkingDirectoryAsync()
    {
        IPostRunHook hook = BackendAnswering(new Reply(Fixture("export.json")));
        string script = File.ReadAllText(Path.Combine(scratch, "opencode")).Replace("exit $(cat", "pwd >> '" + Path.Combine(scratch, "cwd.txt") + "'\nprintenv MY_RUN_VAR >> '" + Path.Combine(scratch, "env.txt") + "'\nexit $(cat", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(scratch, "opencode"), script);
        PostRunContext context = Context() with { Spec = new ProcessSpec("opencode", [], scratch, new Dictionary<string, string> { ["MY_RUN_VAR"] = "from-the-run" }, []) };

        _ = await hook.PostRunAsync(Parsed(), context, baseline: null, TestContext.Current.CancellationToken);

        Assert.Equal("from-the-run", File.ReadAllText(Path.Combine(scratch, "env.txt")).Trim());
        Assert.Equal(Path.GetFullPath(scratch), Path.GetFullPath(File.ReadAllText(Path.Combine(scratch, "cwd.txt")).Trim()));
    }
}
