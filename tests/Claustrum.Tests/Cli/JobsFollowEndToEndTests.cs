using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #123 `claustrum jobs follow [<id>] [--raw]` through the built binary, over hand-made job directories (the files a run writes:
// request.json, backend.json at job start, stdout.log as it grows, result.json at the end), so the poll loop is watched doing what
// a real run would make it do: formatting by the job's backend, holding back a half line, waiting out a half-written result.json
// and a backend.json that is not there yet, and - with no id - following only jobs that appear after it started.
public sealed class JobsFollowEndToEndTests : IDisposable
{
    private static readonly TimeSpan wait = TimeSpan.FromSeconds(30);

    private const string SaysHello = /*lang=json,strict*/ """{"type":"assistant","timestamp":"2026-10-15T09:00:00.000Z","message":{"content":[{"type":"text","text":"hello there"}]}}""";
    private const string SaysDone = /*lang=json,strict*/ """{"type":"assistant","timestamp":"2026-10-15T09:00:05.000Z","message":{"content":[{"type":"text","text":"all done"}]}}""";
    private const string Result = /*lang=json,strict*/ """{"status":"success","backend":"claude","model":"haiku","cost_usd":0.0123,"duration_seconds":2.5,"warnings":["w1","w2"]}""";

    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JobDirectory(string id) => Path.Combine(repo.JobsRoot, id);

    private string MakeJob(string id, string? stdoutLog = "", bool backendJson = true, string? resultJson = null)
    {
        string directory = Directory.CreateDirectory(JobDirectory(id)).FullName;
        File.WriteAllText(Path.Combine(directory, "request.json"), $$$"""{"role":"builder","cwd":"{{{repo.Repo.Replace("\\", "\\\\", StringComparison.Ordinal)}}}"}""");
        if (stdoutLog is not null)
            File.WriteAllText(Path.Combine(directory, "stdout.log"), stdoutLog);

        if (backendJson)
            File.WriteAllText(Path.Combine(directory, "backend.json"), /*lang=json,strict*/ """{"backend":"claude","model":"haiku"}""");

        if (resultJson is not null)
            File.WriteAllText(Path.Combine(directory, "result.json"), resultJson);

        return directory;
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void RequirePosixSignals()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("a console process cannot be sent SIGINT from here on Windows.");
    }

    // ---- one job -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFinishedJobIsFormattedByItsBackendAndEndsWithTheSummaryAsync()
    {
        MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n" + SaysDone + "\n", resultJson: Result);

        CliResult process = await repo.RunAsync("jobs", "follow", "20261015-163517-22930bff");

        string[] lines = Lf(process.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("job 20261015-163517-22930bff  claude:haiku", lines[0]);
        Assert.EndsWith("SAYS  hello there", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("SAYS  all done", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("━━━ success  cost $0.01", lines[3], StringComparison.Ordinal);
        Assert.Contains("2.5s  claude:haiku  2 warnings", lines[3], StringComparison.Ordinal);
        Assert.Equal("", process.Stdout);
    }

    [Fact]
    public async Task RawShowsTheLogLinesAsTheBackendWroteThemAsync()
    {
        MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n", resultJson: Result);

        CliResult process = await repo.RunAsync("jobs", "follow", "20261015-163517-22930bff", "--raw");

        Assert.Contains(SaysHello, Lf(process.Stderr).Split('\n'));
    }

    [Fact]
    public async Task AnUnknownJobIsAUsageErrorAsync()
    {
        CliResult process = await repo.RunAsync("jobs", "follow", "20261015-000000-deadbeef");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("job '20261015-000000-deadbeef' not found under ", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningJobIsFollowedAsBytesAreAppendedAndAHalfWrittenLineIsHeldBackUntilItsNewlineAsync()
    {
        string directory = MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n" + SaysDone[..40]);
        using RunningCli follow = repo.StartCli("jobs", "follow", "20261015-163517-22930bff");

        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  hello there", StringComparison.Ordinal), "the first complete line", wait, Ct);
        await Task.Delay(1500, Ct);
        Assert.DoesNotContain("all done", follow.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", follow.Stderr, StringComparison.Ordinal);

        await File.AppendAllTextAsync(Path.Combine(directory, "stdout.log"), SaysDone[40..], Ct);
        await Task.Delay(1500, Ct);
        Assert.DoesNotContain("all done", follow.Stderr, StringComparison.Ordinal);

        await File.AppendAllTextAsync(Path.Combine(directory, "stdout.log"), "\n", Ct);
        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  all done", StringComparison.Ordinal), "the completed second line", wait, Ct);
        Assert.False(follow.HasExited, "the job has no result yet");

        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), Result, Ct);
        int exit = await follow.WaitForExitAsync(wait, Ct);

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains("━━━ success", follow.Stderr, StringComparison.Ordinal);
    }

    // result.json is written non-atomically: a document that does not parse yet is asked for again on the next poll.
    [Fact]
    public async Task AHalfWrittenResultJsonIsRetriedUntilItParsesAsync()
    {
        string directory = MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n", resultJson: """{"status":"succ""");
        using RunningCli follow = repo.StartCli("jobs", "follow", "20261015-163517-22930bff");

        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  hello there", StringComparison.Ordinal), "the log line", wait, Ct);
        await Task.Delay(2500, Ct);
        Assert.False(follow.HasExited, "an unparsable result.json is retried, not reported");
        Assert.DoesNotContain("incomplete", follow.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("━━━", follow.Stderr, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), Result, Ct);
        int exit = await follow.WaitForExitAsync(wait, Ct);

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Contains("━━━ success", follow.Stderr, StringComparison.Ordinal);
    }

    // backend.json is written at job start but a follow may begin first: until it names the backend the log is held back, then
    // formatted from the beginning.
    [Fact]
    public async Task AMissingBackendJsonIsRetriedUntilItAppearsAndTheLogIsThenFormattedAsync()
    {
        string directory = MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n", backendJson: false);
        using RunningCli follow = repo.StartCli("jobs", "follow", "20261015-163517-22930bff");

        await follow.WaitUntilAsync(() => follow.Stderr.Contains("job 20261015-163517-22930bff", StringComparison.Ordinal), "the header", wait, Ct);
        await Task.Delay(2000, Ct);
        Assert.DoesNotContain("hello there", follow.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", follow.Stderr, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, "backend.json"), /*lang=json,strict*/ """{"backend":"claude","model":"haiku"}""", Ct);
        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  hello there", StringComparison.Ordinal), "the formatted line once the backend is known", wait, Ct);

        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), Result, Ct);
        Assert.Equal(ExitCodes.Ok, await follow.WaitForExitAsync(wait, Ct));
    }

    [Fact]
    public async Task AFinishedJobWithNoBackendJsonIsPrintedRawAndSummarisedAsync()
    {
        MakeJob("20261015-163517-22930bff", stdoutLog: SaysHello + "\n", backendJson: false, resultJson: Result);

        CliResult process = await repo.RunAsync("jobs", "follow", "20261015-163517-22930bff");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Contains(SaysHello, Lf(process.Stderr), StringComparison.Ordinal);
        Assert.Contains("━━━ success", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineTheFormatterCannotReadIsPassedThroughNotFatalAsync()
    {
        MakeJob("20261015-163517-22930bff", stdoutLog: "[1,2]\nnot json at all\n" + SaysHello + "\n", resultJson: Result);

        CliResult process = await repo.RunAsync("jobs", "follow", "20261015-163517-22930bff");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Contains("[1,2]", Lf(process.Stderr).Split('\n'));
        Assert.Contains("not json at all", Lf(process.Stderr).Split('\n'));
        Assert.Contains("SAYS  hello there", process.Stderr, StringComparison.Ordinal);
    }

    // ---- every new job ---------------------------------------------------------------------------------------------

    // Regression F7 (2026-10-15): jobs on disk when the command starts are history; ids as long as 24 characters are picked up.
    [Fact]
    public async Task WithNoIdOnlyJobsThatAppearAfterTheStartAreFollowedAsync()
    {
        RequirePosixSignals();
        MakeJob("20260101-000000-aaaaaaaa", stdoutLog: SaysHello + "\n");
        using RunningCli follow = repo.StartCli("jobs", "follow");
        await Task.Delay(1500, Ct);

        string directory = MakeJob("20261015-163517-22930bff", stdoutLog: SaysDone + "\n");
        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  all done", StringComparison.Ordinal), "the new job's line", wait, Ct);

        string stderr = Lf(follow.Stderr);
        Assert.Equal(24, "20261015-163517-22930bff".Length);
        Assert.Contains("following 20261015-163517-22930bff  job 20261015-163517-22930bff  claude:haiku", stderr, StringComparison.Ordinal);
        Assert.Contains(stderr.Split('\n'), line => line.StartsWith("20261015-163517-22930bff ", StringComparison.Ordinal) && line.EndsWith("SAYS  all done", StringComparison.Ordinal));
        Assert.DoesNotContain("20260101-000000-aaaaaaaa", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("hello there", stderr, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), Result, Ct);
        await follow.WaitUntilAsync(() => follow.Stderr.Contains("━━━ success", StringComparison.Ordinal), "the new job's summary", wait, Ct);

        await follow.SendSigintAsync(Ct);
        Assert.Equal(ExitCodes.Cancelled, await follow.WaitForExitAsync(wait, Ct));
    }

    [Fact]
    public async Task AJobThatHasNoLogYetIsPickedUpWhenItsLogAppearsAsync()
    {
        RequirePosixSignals();
        using RunningCli follow = repo.StartCli("jobs", "follow");
        await Task.Delay(1000, Ct);
        string directory = MakeJob("20261015-170000-0badc0de", stdoutLog: null);
        await Task.Delay(1500, Ct);
        Assert.DoesNotContain("following", follow.Stderr, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), SaysHello + "\n", Ct);
        await follow.WaitUntilAsync(() => follow.Stderr.Contains("SAYS  hello there", StringComparison.Ordinal), "the line once the log exists", wait, Ct);

        await follow.SendSigintAsync(Ct);
        Assert.Equal(ExitCodes.Cancelled, await follow.WaitForExitAsync(wait, Ct));
    }

    [Fact]
    public async Task FollowingTwoJobsPrefixesEachLineWithItsIdAsync()
    {
        RequirePosixSignals();
        using RunningCli follow = repo.StartCli("jobs", "follow");
        await Task.Delay(1000, Ct);
        MakeJob("20261015-170000-aaaaaaaa", stdoutLog: SaysHello + "\n");
        MakeJob("20261015-170000-bbbbbbbb", stdoutLog: SaysDone + "\n");

        await follow.WaitUntilAsync(() => Lf(follow.Stderr).Contains("SAYS  all done", StringComparison.Ordinal) && Lf(follow.Stderr).Contains("SAYS  hello there", StringComparison.Ordinal), "both jobs' lines", wait, Ct);

        string[] lines = Lf(follow.Stderr).Split('\n');
        Assert.Contains(lines, line => line.StartsWith("20261015-170000-aaaaaaaa ", StringComparison.Ordinal) && line.EndsWith("SAYS  hello there", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("20261015-170000-bbbbbbbb ", StringComparison.Ordinal) && line.EndsWith("SAYS  all done", StringComparison.Ordinal));
        await follow.SendSigintAsync(Ct);
        Assert.Equal(ExitCodes.Cancelled, await follow.WaitForExitAsync(wait, Ct));
    }
}
