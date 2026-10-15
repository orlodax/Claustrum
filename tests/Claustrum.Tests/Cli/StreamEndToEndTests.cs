using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #123 through the built binary, with a stand-in `claude` that prints stream-json events: `--stream` is on with no value and
// means "readable" (one line per event, F6), `--stream raw` is the old echo byte for byte, no `--stream` echoes nothing, and
// `run` and `coordinate` agree. Everything the stand-in prints goes to stderr only through the stream flag; the receipt stays one
// document on stdout.
public sealed class StreamEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string[] Events() =>
    [
        /*lang=json,strict*/ """{"type":"system","subtype":"init","cwd":"x","session_id":"s1"}""",
        /*lang=json,strict*/ """{"type":"assistant","timestamp":"2026-10-15T09:00:00.000Z","message":{"content":[{"type":"text","text":"working on it"}]}}""",
        $$$"""{"type":"assistant","timestamp":"2026-10-15T09:00:01.000Z","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"{{{Path.Combine(repo.Repo, "src", "a.cs")}}}"}}]}}""",
        /*lang=json,strict*/ """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"permission denied"}]}}""",
        /*lang=json,strict*/ """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""",
    ];

    private FakeClaudeScript Script() => new() { StreamLines = Events() };

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertReadable(string stderr)
    {
        string text = Lf(stderr);
        Assert.Contains("SAYS  working on it", text, StringComparison.Ordinal);
        Assert.Contains("EDIT  src", text, StringComparison.Ordinal);
        Assert.Contains("✗     permission denied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"type\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rate_limit", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", text, StringComparison.Ordinal);
    }

    private static JsonElement Receipt(CliResult process)
    {
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return document.RootElement.Clone();
    }

    // Regression F6 (2026-10-15): a bare `--stream` parsed to a null value and so meant "off". Both spots are tried: last, and
    // before another option, which a ZeroOrOne option must not swallow.
    [Theory]
    [InlineData("--json", "--stream")]
    [InlineData("--stream", "--json")]
    [InlineData("--stream", "readable")]
    public async Task StreamWithNoValueIsOnAndReadableSameAsReadableAsync(params string[] tail)
    {
        FakeClaude.RequirePosixShell();
        repo.Script(Script());
        string[] args = tail.Contains("readable") ? ["run", "builder", "--brief", "do the task", "--json", .. tail] : ["run", "builder", "--brief", "do the task", .. tail];

        CliResult process = await repo.RunAsync(args);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        AssertReadable(process.Stderr);
        JsonElement receipt = Receipt(process);
        Assert.Equal("success", receipt.GetProperty("status").GetString());
        Assert.Equal(0.01m, receipt.GetProperty("cost_usd").GetDecimal());
    }

    // The old echo: every line the backend wrote, as it wrote it - the events and the final result line - nothing else on stderr.
    [Fact]
    public async Task StreamRawEchoesEveryBackendLineByteForByteAsync()
    {
        FakeClaude.RequirePosixShell();
        FakeClaudeScript script = Script();
        repo.Script(script);

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--json", "--stream", "raw");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal(string.Join('\n', [.. Events(), FakeClaude.ReplyLine(script)]) + "\n", Lf(process.Stderr));
        Assert.Equal("success", Receipt(process).GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithoutStreamNothingIsEchoedAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(Script());

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal("", process.Stderr);
    }

    [Fact]
    public async Task AnUnknownStreamModeIsAUsageErrorNamingTheChoicesAsync()
    {
        repo.Script(Script());

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--stream", "loud");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("readable", process.Stderr, StringComparison.Ordinal);
        Assert.Contains("raw", process.Stderr, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
    }

    [Fact]
    public async Task TheJobLogKeepsTheRawEventsWhateverTheStderrShowsAsync()
    {
        FakeClaude.RequirePosixShell();
        FakeClaudeScript script = Script();
        repo.Script(script);

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task", "--json", "--stream");

        string jobId = Assert.Single(repo.JobDirectories());
        string log = Lf(File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "stdout.log")));
        Assert.Equal(string.Join('\n', [.. Events(), FakeClaude.ReplyLine(script)]) + "\n", log);
        AssertReadable(process.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinateStreamsTheSameWayAsRunAsync(bool raw)
    {
        FakeClaude.RequirePosixShell();
        using IsolatedRepo coordinate = IsolatedRepo.ForCoordinate();
        FakeClaudeScript script = new() { StreamLines = [.. Events().Select(line => line.Replace(repo.Repo, coordinate.Repo, StringComparison.Ordinal))] };
        coordinate.Script(script);

        CliResult process = raw
            ? await coordinate.RunAsync("coordinate", "--brief", "do the task", "--json", "--stream", "raw")
            : await coordinate.RunAsync("coordinate", "--brief", "do the task", "--json", "--stream");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        if (raw)
            Assert.Contains("{\"type\":\"assistant\"", Lf(process.Stderr), StringComparison.Ordinal);
        else
        {
            Assert.Contains("SAYS  working on it", Lf(process.Stderr), StringComparison.Ordinal);
            Assert.DoesNotContain("{\"type\"", Lf(process.Stderr), StringComparison.Ordinal);
        }
    }
}
