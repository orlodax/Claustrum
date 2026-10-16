using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Jobs;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 2 through the built binary: a `run` in a coordinate tree whose architect is live (CLAUSTRUM_PARENT_JOB and
// CLAUSTRUM_ARCHITECT_LIVE=1) is hosted detached and returns 7 on its child's question; `jobs reply` answers it. The root is a
// job directory with a live `sleep` as its owner, which is all a child's watch of its root looks at. DetachedRepo kills every host.
public sealed class TreeRoutingEndToEndTests : IDisposable
{
    private static readonly TimeSpan exitLimit = TimeSpan.FromSeconds(30);

    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private Dictionary<string, string?> InTree(string? architectLive = "1") => DetachedRepo.Tree(fixture.MintRoot(), architectLive);

    private static FakeClaudeScript Asks(params string[] questions) => new()
    {
        Turns = [.. questions.Select(question => FakeClaudeTurn.Ask(question)), FakeClaudeTurn.Report("final", echoReply: true)],
    };

    // A turn that takes long enough for the test to act while the job is plainly running, then ends as a question nobody reads.
    private static FakeClaudeScript Working() => new() { Turns = [new FakeClaudeTurn("still working", SleepSeconds: 25)] };

    private static JsonElement OneDocument(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- a question returns 7 -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AQuestionReturnsExitSevenWithOneNeedsInputDocumentAndTheChildKeepsWaitingAsync()
    {
        fixture.Repo.Script(Asks("which db?"));

        CliResult process = await fixture.RunBuilderAsync(InTree(), "--json");

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal(7, ExitCodes.NeedsInput);
        JsonElement document = OneDocument(process.Stdout);
        Assert.Equal(["status", "job_id", "question"], document.EnumerateObject().Select(property => property.Name));
        Assert.Equal("needs_input", document.GetProperty("status").GetString());
        string id = document.GetProperty("job_id").GetString() ?? "";
        Assert.Equal(id, fixture.OnlyJob());
        Assert.Equal(1, document.GetProperty("question").GetProperty("seq").GetInt32());
        Assert.Equal("which db?", document.GetProperty("question").GetProperty("text").GetString());

        // Exactly one document, and the child is still there: its host, its session, no receipt, and the 1800 s reply window of a tree's child.
        Assert.True(fixture.HostAlive(id));
        Assert.Equal("waiting_for_input", fixture.LiveState(id));
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "result.json")));
        Assert.Equal(1800, fixture.Json(id, "live.json")?.GetProperty("reply_timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task InHumanModeTheQuestionIsPrintedAfterTheJobIdAndNothingElseAsync()
    {
        fixture.Repo.Script(Asks("which db?"));

        CliResult process = await fixture.RunBuilderAsync(InTree());

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        string id = fixture.OnlyJob();
        Assert.Equal([$"needs input: {id}", "? which db?"], Lf(process.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task ReplyLoopsSevenSevenZeroAndPrintsTheFinalRunResultAsync()
    {
        fixture.Repo.Script(Asks("q1", "q2"));
        CliResult first = await fixture.RunBuilderAsync(InTree(), "--json");
        string id = OneDocument(first.Stdout).GetProperty("job_id").GetString() ?? "";

        CliResult second = await fixture.Repo.RunAsync("jobs", "reply", id, "a1", "--json");
        CliResult last = await fixture.Repo.RunAsync("jobs", "reply", id, "a2");

        Assert.Equal(ExitCodes.NeedsInput, first.ExitCode);
        Assert.Equal(ExitCodes.NeedsInput, second.ExitCode);
        JsonElement asksAgain = OneDocument(second.Stdout);
        Assert.Equal("needs_input", asksAgain.GetProperty("status").GetString());
        Assert.Equal(id, asksAgain.GetProperty("job_id").GetString());
        Assert.Equal("q2", asksAgain.GetProperty("question").GetProperty("text").GetString());
        // The conversation counts every entry: the question (1), the reply a1 (2), the next question (3).
        Assert.Equal(3, asksAgain.GetProperty("question").GetProperty("seq").GetInt32());

        Assert.True(last.ExitCode == ExitCodes.Ok, last.Stderr);
        Assert.StartsWith("status: Success (exit 0)", Lf(last.Stdout), StringComparison.Ordinal);
        Assert.Contains("you said: a2", last.Stdout, StringComparison.Ordinal);
        Assert.Equal("success", fixture.ResultStatus(id));
        string conversation = fixture.Conversation(id);
        Assert.Contains("\"text\":\"a1\"", conversation, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"a2\"", conversation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyJsonPrintsTheRunResultAsTheOneDocumentWhenTheJobFinishesAsync()
    {
        fixture.Repo.Script(Asks("q1"));
        string id = OneDocument((await fixture.RunBuilderAsync(InTree(), "--json")).Stdout).GetProperty("job_id").GetString() ?? "";

        CliResult last = await fixture.Repo.RunAsync("jobs", "reply", id, "a1", "--json");

        Assert.True(last.ExitCode == ExitCodes.Ok, last.Stderr);
        JsonElement result = OneDocument(last.Stdout);
        Assert.Equal("success", result.GetProperty("status").GetString());
        Assert.Equal(id, result.GetProperty("job_id").GetString());
        Assert.Equal("builder", result.GetProperty("role").GetString());
    }

    [Fact]
    public async Task ReplyIsRefusedForAnUnknownADoneAndAnAbandonedJobAsync()
    {
        fixture.Repo.Script(Asks("q1"));
        string id = OneDocument((await fixture.RunBuilderAsync(InTree(), "--json")).Stdout).GetProperty("job_id").GetString() ?? "";
        await fixture.Repo.RunAsync("jobs", "reply", id, "a1");

        CliResult done = await fixture.Repo.RunAsync("jobs", "reply", id, "again");
        CliResult unknown = await fixture.Repo.RunAsync("jobs", "reply", "20261015-000000-deadbeef", "hello");

        Assert.Equal(ExitCodes.Usage, done.ExitCode);
        Assert.Contains("is done", done.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
    }

    // ---- what is not routed -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("true")]
    public async Task WithoutALiveArchitectNothingIsRoutedAndTheReplyTimeoutIsZeroAsync(string? architectLive)
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        CliResult process = await fixture.RunBuilderAsync(InTree(architectLive), "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string id = fixture.OnlyJob();
        Assert.Equal("success", OneDocument(process.Stdout).GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "host.json")), "run in-process: no host");
        Assert.Equal(0, fixture.Json(id, "live.json")?.GetProperty("reply_timeout_seconds").GetInt32());
        Assert.Contains("No human is available to answer now.", fixture.Conversation(id), StringComparison.Ordinal);
        Assert.Contains("stop with status needs_input", fixture.Conversation(id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitReplyTimeoutStillWinsWithoutALiveArchitectAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        CliResult process = await fixture.RunBuilderAsync(InTree("0"), "--json", "--reply-timeout", "1");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal(1, fixture.Json(fixture.OnlyJob(), "live.json")?.GetProperty("reply_timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task AStreamedRunInATreeStaysInProcessWithNobodyToAnswerAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("alone")] });

        CliResult process = await fixture.RunBuilderAsync(InTree(), "--json", "--stream");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string id = fixture.OnlyJob();
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "host.json")));
        Assert.Equal(0, fixture.Json(id, "live.json")?.GetProperty("reply_timeout_seconds").GetInt32());
        Assert.Contains("No human is available to answer now.", fixture.Conversation(id), StringComparison.Ordinal);
        Assert.Contains("stop with status needs_input", fixture.Conversation(id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLiveInATreeIsNotHostedAndHasNoSessionAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Summary = "plain" });

        CliResult process = await fixture.RunBuilderAsync(InTree(), "--json", "--no-live");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string id = fixture.OnlyJob();
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "host.json")));
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "live.json")));
    }

    [Fact]
    public async Task ABlindChildIsNotHostedAndNeverReturnsSevenAsync()
    {
        fixture.Repo.WriteCast(maxParallel: null, role: "code-reviewer");
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("reviewed")] });

        CliResult process = await fixture.Repo.RunWithEnvAsync(["run", "code-reviewer", "--brief", "review it", "--json"], InTree());

        Assert.NotEqual(ExitCodes.NeedsInput, process.ExitCode);
        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement result = OneDocument(process.Stdout);
        Assert.Equal("success", result.GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(result.GetProperty("job_id").GetString() ?? ""), "host.json")));
    }

    // ---- exit codes and documents ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMachineryRefusalInATreeExitsTwoLikeInProcessAsync(bool jsonMode)
    {
        fixture.Repo.WriteCast(maxParallel: 1);
        string outside = Directory.CreateTempSubdirectory("claustrum-outside-").FullName;
        try
        {
            TestLinks.CreateDirectoryLink(Path.Combine(fixture.Repo.Repo, ".claustrum", "locks"), outside);

            CliResult process = await fixture.RunBuilderAsync(InTree(), jsonMode ? ["--json"] : []);

            Assert.Equal(ExitCodes.Usage, process.ExitCode);
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            string id = fixture.OnlyJob();
            Assert.True(File.Exists(Path.Combine(fixture.JobDir(id), "refused")), "the refusal marker precedes the receipt");
            Assert.Equal("failed", fixture.ResultStatus(id));
            if (jsonMode)
            {
                JsonElement result = OneDocument(process.Stdout);
                Assert.Equal("failed", result.GetProperty("status").GetString());
                Assert.Contains("resolves outside the working directory", result.GetProperty("error").GetString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("resolves outside the working directory", process.Stderr, StringComparison.Ordinal);
            }
        }
        finally
        {
            TempTree.Delete(outside);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHostThatDiesWithoutAReceiptEndsRunWithExitSixAndOneSynthesizedResultAsync(bool jsonMode)
    {
        fixture.Repo.Script(Working());
        using RunningCli run = fixture.StartBuilder(InTree(), jsonMode ? ["--json"] : []);
        string id = await fixture.WaitForJobAsync(job => fixture.LiveState(job) == "running", "the hosted job to run");

        fixture.KillHost(id);
        int exit = await run.WaitForExitAsync(exitLimit, DetachedRepo.Ct);

        Assert.Equal(ExitCodes.Abandoned, exit);
        Assert.Equal(6, ExitCodes.Abandoned);
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "result.json")), "no receipt is invented on disk");
        if (jsonMode)
        {
            JsonElement result = OneDocument(run.Stdout);
            Assert.Equal("failed", result.GetProperty("status").GetString());
            Assert.Equal(id, result.GetProperty("job_id").GetString());
            Assert.Contains("abandoned: the claustrum process running this job is gone", result.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Contains("abandoned", run.Stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("abandoned: the claustrum process running this job is gone", run.Stdout.Trim());
        }
    }

    [Fact]
    public async Task ReplyJsonToAJobWhoseHostDiesPrintsOneSynthesizedResultAndExitsSixAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("q1"), new FakeClaudeTurn("still working", SleepSeconds: 25)] });
        string id = OneDocument((await fixture.RunBuilderAsync(InTree(), "--json")).Stdout).GetProperty("job_id").GetString() ?? "";
        using RunningCli reply = fixture.Repo.StartCli("jobs", "reply", id, "go", "--json");
        await DetachedRepo.WaitUntilAsync(() => fixture.LiveState(id) == "running", "the reply to reach the session");

        fixture.KillHost(id);
        int exit = await reply.WaitForExitAsync(exitLimit, DetachedRepo.Ct);

        Assert.Equal(ExitCodes.Abandoned, exit);
        JsonElement result = OneDocument(reply.Stdout);
        Assert.Equal("failed", result.GetProperty("status").GetString());
        Assert.Equal(id, result.GetProperty("job_id").GetString());
    }

    [Fact]
    public async Task CtrlCOnAWaitingRunCancelsTheJobThroughItsHostAndPrintsTheCancelledReceiptAsync()
    {
        fixture.Repo.Script(Working());
        using RunningCli run = fixture.StartBuilder(InTree(), "--json");
        string id = await fixture.WaitForJobAsync(job => fixture.LiveState(job) == "running", "the hosted job to run");
        if (run.IgnoresSignal(2))
            Assert.Skip("the test host started with SIGINT ignored, so a Ctrl-C would do nothing.");

        await run.SendSigintAsync(DetachedRepo.Ct);
        int exit = await run.WaitForExitAsync(exitLimit, DetachedRepo.Ct);

        Assert.Equal(ExitCodes.Cancelled, exit);
        JsonElement result = OneDocument(run.Stdout);
        Assert.Equal("cancelled", result.GetProperty("status").GetString());
        Assert.Equal(id, result.GetProperty("job_id").GetString());
        Assert.Equal("cancelled", fixture.ResultStatus(id));
        await fixture.WaitForHostGoneAsync(id);
    }

    [Fact]
    public async Task CtrlCThatCannotWriteTheCancelMarkerPrintsOneCancelledResultAndExitsOneThirtyAsync()
    {
        fixture.Repo.Script(Working());
        using RunningCli run = fixture.StartBuilder(InTree(), "--json");
        string id = await fixture.WaitForJobAsync(job => fixture.LiveState(job) == "running", "the hosted job to run");
        if (run.IgnoresSignal(2))
            Assert.Skip("the test host started with SIGINT ignored, so a Ctrl-C would do nothing.");

        // A marker that is a link leaving the jobs store: `jobs cancel` and the waiter refuse to write through it.
        string outside = Directory.CreateTempSubdirectory("claustrum-outside-").FullName;
        try
        {
            TestLinks.Create(Path.Combine(fixture.JobDir(id), "cancel"), Path.Combine(outside, "never"));

            await run.SendSigintAsync(DetachedRepo.Ct);
            int exit = await run.WaitForExitAsync(exitLimit, DetachedRepo.Ct);

            // macOS CI failed here three ways (2026-10-15/16) and the receipt alone never said who cancelled the job: every check
            // below reports everything the run left, conversation (a root-watch notice would be there) and marker included.
            string receipt = Path.Combine(fixture.JobDir(id), "result.json");
            string conversation = Path.Combine(fixture.JobDir(id), "conversation.jsonl");
            string marker = Path.Combine(fixture.JobDir(id), "cancel");
            string Evidence() =>
                $"exit {exit}\nstdout: {run.Stdout}\nstderr: {run.Stderr}\nresult.json: {(File.Exists(receipt) ? File.ReadAllText(receipt) : "(none)")}"
                + $"\nconversation: {(File.Exists(conversation) ? File.ReadAllText(conversation) : "(none)")}"
                + $"\ncancel marker: {(new FileInfo(marker).LinkTarget is { } target ? $"link to {target}" : File.Exists(marker) ? "a plain file" : "absent")}"
                + $"; outside: [{string.Join(", ", Directory.GetFileSystemEntries(outside))}]; host alive: {fixture.HostAlive(id)}"
                + $"\nhost.log: {(File.Exists(Path.Combine(fixture.JobDir(id), "host.log")) ? File.ReadAllText(Path.Combine(fixture.JobDir(id), "host.log")) : "(none)")}";
            Assert.True(exit == ExitCodes.Cancelled, Evidence());
            JsonElement result = OneDocument(run.Stdout);
            Assert.True(result.GetProperty("status").GetString() == "cancelled", Evidence());
            Assert.Equal(id, result.GetProperty("job_id").GetString());
            Assert.True((result.GetProperty("error").GetString() ?? "").Contains("could not be made", StringComparison.Ordinal), Evidence());
            Assert.True(Directory.GetFileSystemEntries(outside).Length == 0, Evidence());
            Assert.True(fixture.HostAlive(id), $"the job was not told to stop, so its host is still running it\n{Evidence()}");
        }
        finally
        {
            fixture.KillHost(id);
            TempTree.Delete(outside);
        }
    }

    [Fact]
    public async Task EveryHostedChildIsRecordedUnderItsTreeAsync()
    {
        fixture.Repo.Script(Asks("q1"));
        Dictionary<string, string?> env = InTree();

        CliResult process = await fixture.RunBuilderAsync(env, "--json");

        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal(env["CLAUSTRUM_PARENT_JOB"], fixture.Host(fixture.OnlyJob())?.Tree);
        Assert.True(JobDirectory.IsJobId(fixture.OnlyJob()));
    }
}
