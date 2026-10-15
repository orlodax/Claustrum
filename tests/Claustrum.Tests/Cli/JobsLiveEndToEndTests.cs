using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Json;
using Claustrum.Core.Live;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 `jobs say`, `jobs wait`, `jobs follow` and the `run` flags through the built binary, over hand-made live job directories
// (live.json, conversation.jsonl, inbox/, request.json, result.json: the files a live run writes), and one real run against a conversing fake claude.
public sealed class JobsLiveEndToEndTests : IDisposable
{
    private const string Id = "20261015-190000-aaaaaaaa";

    // Not a process on any machine: pids stop well below it, so GetProcessById refuses it.
    private const int GonePid = int.MaxValue - 1;

    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string JobDir => Path.Combine(repo.JobsRoot, Id);

    private string MakeJob(string role = "builder", LiveStatus? state = LiveStatus.Running, int? questionSeq = null, int ownerPid = 0, ConversationEntry[]? entries = null, string? result = null)
    {
        Directory.CreateDirectory(JobDir);
        File.WriteAllText(Path.Combine(JobDir, "request.json"), $$$"""{"role":"{{{role}}}","cwd":"{{{repo.Repo.Replace("\\", "\\\\", StringComparison.Ordinal)}}}"}""");
        File.WriteAllText(Path.Combine(JobDir, "backend.json"), /*lang=json,strict*/ """{"backend":"claude","model":"haiku"}""");
        if (state is { } status)
        {
            LiveState live = new(status, questionSeq, DateTimeOffset.UtcNow, 60, ownerPid == 0 ? Environment.ProcessId : ownerPid);
            File.WriteAllText(Path.Combine(JobDir, "live.json"), JsonSerializer.Serialize(live, ClaustrumJsonContext.Default.LiveState));
        }

        File.WriteAllLines(Path.Combine(JobDir, "conversation.jsonl"), (entries ?? []).Select(entry => JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.ConversationEntry)));
        if (result is not null)
            File.WriteAllText(Path.Combine(JobDir, "result.json"), result);

        return JobDir;
    }

    private static ConversationEntry Entry(int seq, ConversationParty from, ConversationKind kind, string text) => new(seq, DateTimeOffset.UtcNow, from, kind, text);

    private static ConversationEntry Question(int seq, string text) => Entry(seq, ConversationParty.Role, ConversationKind.Question, text);

    private string[] Inbox() => Directory.Exists(Path.Combine(JobDir, "inbox")) ? Directory.GetFileSystemEntries(Path.Combine(JobDir, "inbox")) : [];

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private const string DoneReceipt = /*lang=json,strict*/ """{"status":"success","backend":"claude","model":"haiku","cost_usd":0.0123,"duration_seconds":2.5,"warnings":[]}""";

    // ---- jobs say ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SayQueuesAMessageForARunningJobAtomicallyAsync()
    {
        MakeJob();

        CliResult process = await repo.RunAsync("jobs", "say", Id, "go left");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Equal(["queued: message", "state: running"], Lf(process.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        string file = Assert.Single(Inbox());
        Assert.EndsWith(".json", file, StringComparison.Ordinal);
        InboxMessage? message = JsonSerializer.Deserialize(File.ReadAllText(file), ClaustrumJsonContext.Default.InboxMessage);
        Assert.Equal("go left", message?.Text);
    }

    [Fact]
    public async Task SayToAnOpenQuestionIsItsAnswerAsync()
    {
        MakeJob(state: LiveStatus.WaitingForInput, questionSeq: 1, entries: [Question(1, "which?")]);

        CliResult process = await repo.RunAsync("jobs", "say", Id, "this one");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Contains("queued: answer to question 1", process.Stdout, StringComparison.Ordinal);
        Assert.Contains("state: waiting_for_input", process.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notlive", "is not live")]
    [InlineData("done", "is done")]
    [InlineData("blind", "blind role")]
    [InlineData("abandoned", "abandoned: the claustrum process running this job is gone")]
    public async Task SayIsRefusedForAJobThatCannotHearAsync(string kind, string reason)
    {
        _ = kind switch
        {
            "notlive" => MakeJob(state: null),
            "done" => MakeJob(state: LiveStatus.Done, result: DoneReceipt),
            "blind" => MakeJob(role: "code-reviewer"),
            _ => MakeJob(ownerPid: GonePid),
        };

        CliResult process = await repo.RunAsync("jobs", "say", Id, "hello");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(reason, process.Stderr, StringComparison.Ordinal);
        Assert.Empty(Inbox());
    }

    [Fact]
    public async Task SayToAnUnknownJobOrWithEmptyTextIsAUsageErrorAsync()
    {
        MakeJob();

        CliResult unknown = await repo.RunAsync("jobs", "say", "20261015-000000-deadbeef", "hi");
        CliResult empty = await repo.RunAsync("jobs", "say", Id, "   ");

        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
        Assert.Equal(ExitCodes.Usage, empty.ExitCode);
        Assert.Contains("nothing to say", empty.Stderr, StringComparison.Ordinal);
    }

    // ---- jobs wait -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WaitReturnsTheOpenQuestionWithExitZeroAsync()
    {
        MakeJob(state: LiveStatus.WaitingForInput, questionSeq: 2, entries: [Entry(1, ConversationParty.Role, ConversationKind.Message, "hm"), Question(2, "which one?\nthe red one?")]);

        CliResult plain = await repo.RunAsync("jobs", "wait", Id);
        CliResult json = await repo.RunAsync("jobs", "wait", Id, "--json");

        Assert.Equal(ExitCodes.Ok, plain.ExitCode);
        Assert.Equal(["? which one?", "  the red one?", "(question 2)"], Lf(plain.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(ExitCodes.Ok, json.ExitCode);
        using JsonDocument document = JsonDocument.Parse(json.Stdout);
        Assert.Equal("waiting_for_input", document.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("question").GetProperty("seq").GetInt32());
        Assert.Equal("which one?\nthe red one?", document.RootElement.GetProperty("question").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("result_status").ValueKind);
    }

    [Fact]
    public async Task WaitOnAFinishedJobPrintsTheSummaryAndItsStatusUnderJsonAsync()
    {
        MakeJob(state: LiveStatus.Done, result: DoneReceipt);

        CliResult plain = await repo.RunAsync("jobs", "wait", Id);
        CliResult json = await repo.RunAsync("jobs", "wait", Id, "--json");

        Assert.Equal(ExitCodes.Ok, plain.ExitCode);
        Assert.StartsWith("━━━ success  cost $0.01", plain.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, json.ExitCode);
        using JsonDocument document = JsonDocument.Parse(json.Stdout);
        Assert.Equal("done", document.RootElement.GetProperty("state").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("result_status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("question").ValueKind);
    }

    [Fact]
    public async Task WaitTimesOutWithExitFourAndASuppliedJsonDocumentAsync()
    {
        MakeJob();

        CliResult process = await repo.RunAsync("jobs", "wait", Id, "--timeout", "1", "--json");

        Assert.Equal(ExitCodes.Timeout, process.ExitCode);
        Assert.Contains("timed out after 1s", process.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("running", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task WaitOnAnAbandonedJobExitsSixAndSaysSoAsync()
    {
        MakeJob(ownerPid: GonePid);

        CliResult plain = await repo.RunAsync("jobs", "wait", Id);
        CliResult json = await repo.RunAsync("jobs", "wait", Id, "--json");

        Assert.Equal(ExitCodes.Abandoned, plain.ExitCode);
        Assert.Equal(6, ExitCodes.Abandoned);
        Assert.Contains("abandoned: the claustrum process running this job is gone", plain.Stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Abandoned, json.ExitCode);
        using JsonDocument document = JsonDocument.Parse(json.Stdout);
        Assert.Equal("abandoned", document.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task WaitWithANonPositiveTimeoutOrAnUnknownJobIsAUsageErrorAsync()
    {
        MakeJob();

        CliResult zero = await repo.RunAsync("jobs", "wait", Id, "--timeout", "0");
        CliResult unknown = await repo.RunAsync("jobs", "wait", "20261015-000000-deadbeef");

        Assert.Equal(ExitCodes.Usage, zero.ExitCode);
        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
    }

    // ---- jobs follow ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FollowRendersTheConversationAsQuestionHumanAndNoticeLinesThroughTheSanitizerAsync()
    {
        MakeJob(
            state: LiveStatus.Done,
            result: DoneReceipt,
            entries:
            [
                Question(1, "delete it?\u001b[31m"),
                Entry(2, ConversationParty.Human, ConversationKind.Reply, "yes"),
                Entry(3, ConversationParty.Claustrum, ConversationKind.Notice, "About 2 minutes remain"),
                Entry(4, ConversationParty.Role, ConversationKind.Message, "already in the stream"),
            ]);

        CliResult process = await repo.RunAsync("jobs", "follow", Id);

        string[] lines = Lf(process.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Contains(lines, line => line.StartsWith("? delete it?", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', process.Stderr);
        Assert.Contains("> yes", lines);
        Assert.Contains("! About 2 minutes remain", lines);
        Assert.DoesNotContain(lines, line => line.Contains("already in the stream", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FollowOfAnAbandonedJobSaysSoOnceAndStopsPromptingAsync()
    {
        MakeJob(ownerPid: GonePid, entries: [Question(1, "still there?")]);
        using RunningCli follow = repo.StartCli("jobs", "follow", Id);

        await follow.WaitUntilAsync(() => follow.Stderr.Contains("abandoned: the claustrum process", StringComparison.Ordinal), "the abandoned notice", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        Assert.Single(Lf(follow.Stderr).Split('\n'), line => line.StartsWith("abandoned: the claustrum process", StringComparison.Ordinal));
        follow.Kill();
    }

    // ---- run: the flags and the TTY rule -------------------------------------------------------------------------

    [Fact]
    public async Task ReplyTimeoutIsAUsageErrorWhenNegativeOrWithNoLiveAsync()
    {
        repo.WriteCast(maxParallel: null);

        CliResult negative = await repo.RunAsync("run", "builder", "--brief", "x", "--reply-timeout", "-1");
        CliResult withNoLive = await repo.RunAsync("run", "builder", "--brief", "x", "--no-live", "--reply-timeout", "5");

        Assert.Equal(ExitCodes.Usage, negative.ExitCode);
        Assert.Contains("--reply-timeout must be zero or more seconds", negative.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, withNoLive.ExitCode);
        Assert.Contains("--reply-timeout has no meaning with --no-live", withNoLive.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutATerminalTheReplyTimeoutIsZeroAndTheRoleIsToldNobodyIsThereAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string job = result.GetProperty("job_id").GetString() ?? "";
        string directory = Path.Combine(repo.JobsRoot, job);
        using JsonDocument live = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "live.json")));
        Assert.Equal(0, live.RootElement.GetProperty("reply_timeout_seconds").GetInt32());
        string conversation = File.ReadAllText(Path.Combine(directory, "conversation.jsonl"));
        Assert.Contains("No human is available to answer now.", conversation, StringComparison.Ordinal);
        Assert.Contains("stop with status needs_input", conversation, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("conversation").ValueKind);
    }

    [Fact]
    public async Task ReplyTimeoutOneWaitsThenTellsTheRoleToDecideAloneAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--reply-timeout", "1");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string directory = Path.Combine(repo.JobsRoot, result.GetProperty("job_id").GetString() ?? "");
        using JsonDocument live = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "live.json")));
        Assert.Equal(1, live.RootElement.GetProperty("reply_timeout_seconds").GetInt32());
        Assert.Contains("No answer arrived within 1 second", File.ReadAllText(Path.Combine(directory, "conversation.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLiveRunsExactlyAsBeforeWithNoConversationFilesAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Summary = "plain", ArgvLog = Path.Combine(repo.Repo, "argv.log") });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--no-live");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("conversation").ValueKind);
        string directory = Path.Combine(repo.JobsRoot, result.GetProperty("job_id").GetString() ?? "");
        Assert.False(File.Exists(Path.Combine(directory, "live.json")));
        Assert.DoesNotContain("--input-format", File.ReadAllLines(Path.Combine(repo.Repo, "argv.log")));
    }
}
