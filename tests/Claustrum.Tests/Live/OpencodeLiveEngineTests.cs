using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// #124 stage 3a, the engine on opencode: Runner + LiveSession + ProcessRunner against a fake `opencode` (FakeOpencode) that is one process per
// turn: `run [--session id] [--] message` prints the turn's events and exits, and `session export` reports the session's running cost.
// The inbox is written the way `jobs say` writes it. The contract is NOTES.md "Live sessions on opencode" and docs/MANUAL.md "Talking to a running job".
public sealed class OpencodeLiveEngineTests
{
    private static LiveJobHarness Job(params FakeOpencodeTurn[] turns) => LiveJobHarness.ForOpencode(turns);

    private static async Task WaitForRunsAsync(LiveJobHarness job, int runs) =>
        await LiveJobHarness.WaitUntilAsync(() => job.Opencode.Runs >= runs, $"opencode process {runs} to start");

    // ---- the turns ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATurnThatAsksWaitsAndTheReplyRunsAsTheSessionsNextProcessAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("which database?"), FakeOpencodeTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();

        LiveState waiting = await job.WaitForQuestionAsync();
        ConversationEntry question = Assert.Single(job.Conversation());
        Assert.Equal(question.Seq, waiting.QuestionSeq);
        Assert.Equal("which database?", question.Text);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, job.Opencode.Runs);

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal(2, job.Opencode.Runs);
        Assert.Equal([ConversationKind.Question, ConversationKind.Reply, ConversationKind.Message], job.Conversation().Select(entry => entry.Kind));
        Assert.Equal(LiveStatus.Done, job.State()?.State);

        // Turn 1 is a plain run's argv plus `--`; turn 2 the same argv with `--session <id>` and the reply as its message, after the `--`.
        string[] first = job.Opencode.Argv(1);
        string[] second = job.Opencode.Argv(2);
        Assert.Equal(["run", "--standalone", "--agent", "claustrum-builder", "--model", "opencode/fake", "--format", "json", "--auto", "--"], first[..10]);
        Assert.StartsWith("do it", first[10], StringComparison.Ordinal);
        Assert.Equal(["run", "--standalone", "--agent", "claustrum-builder", "--model", "opencode/fake", "--format", "json", "--auto", "--session", FakeOpencode.Session, "--", "sqlite"], second);
        Assert.DoesNotContain("--session", first);
        Assert.Equal("sqlite", job.Opencode.Message(2));

        // The receipt's message is the last turn's, not both turns' text run together.
        Assert.Contains("ready", result.FinalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("which database?", result.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageThatArrivesMidTurnWaitsForTheTurnsEndAndAnswersTheQuestionItEndsWithAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("which database?", sleepSeconds: 3), FakeOpencodeTurn.Report("used it"));
        Task<RunResult> run = job.StartAsync();
        await WaitForRunsAsync(job, 1);

        job.Say("sqlite please");
        List<LiveStatus> seen = [];
        while (!run.IsCompleted)
        {
            if (job.State() is { } state)
                seen.Add(state.State);

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        RunResult result = await run;

        // Recorded at once (before the question, which the turn printed at its end), sent as the next turn, and never published as an open wait.
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.DoesNotContain(LiveStatus.WaitingForInput, seen);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Question, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal((ConversationParty.Human, "sqlite please"), (entries[0].From, entries[0].Text));
        Assert.Equal(2, job.Opencode.Runs);
        Assert.Equal("sqlite please", job.Opencode.Message(2));
        Assert.DoesNotContain(entries, entry => entry.Text == LiveSession.UndeliveredNotice);
    }

    [Fact]
    public async Task AMessageStillQueuedWhenTheTurnEndsWithTheReportGetsTheUndeliveredNoticeAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Report("finished", sleepSeconds: 3));
        Task<RunResult> run = job.StartAsync();
        await WaitForRunsAsync(job, 1);

        job.Say("too late");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.From == ConversationParty.Human), "the message to be recorded");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(1, job.Opencode.Runs);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Message, ConversationKind.Notice], entries.Select(entry => entry.Kind));
        Assert.Equal(LiveSession.UndeliveredNotice, entries[^1].Text);
        Assert.Equal(ConversationParty.Claustrum, entries[^1].From);
    }

    [Fact]
    public async Task SeveralMessagesQueuedDuringATurnGoOutAsOneNextTurnJoinedByABlankLineAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("anything else?", sleepSeconds: 3), FakeOpencodeTurn.Report("ok"));
        Task<RunResult> run = job.StartAsync();
        await WaitForRunsAsync(job, 1);

        job.Say("first");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Count(entry => entry.From == ConversationParty.Human) == 1, "the first message");
        job.Say("second");
        await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(2, job.Opencode.Runs);
        Assert.Equal("first\n\nsecond", job.Opencode.Message(2));
    }

    // ---- cost ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshLiveSessionIsBilledTheSessionsExportedTotalAcrossAllItsTurnsAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("q?", costUsd: 0.10m), FakeOpencodeTurn.Report("s", costUsd: 0.05m));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("a");

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(0.15m, result.CostUsd);
        Assert.Equal(1, job.Opencode.Exports);
    }

    [Fact]
    public async Task AResumedLiveSessionIsBilledOnlyWhatItAddedTheExportAfterMinusBeforeAsync()
    {
        using LiveJobHarness job = LiveJobHarness.ForOpencode([FakeOpencodeTurn.Ask("q?", costUsd: 0.10m), FakeOpencodeTurn.Report("s", costUsd: 0.05m)], startingCostUsd: 2.00m);
        Task<RunResult> run = job.StartAsync(resumeSession: FakeOpencode.Session);
        await job.WaitForQuestionAsync();
        job.Say("a");

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(0.15m, result.CostUsd);
        Assert.Equal(2, job.Opencode.Exports);
        Assert.Contains(FakeOpencode.Session, job.Opencode.Argv(1));
    }

    // ---- the logs -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StdoutLogGrowsAcrossTurnsAsOneContinuousFileAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("which database?"), FakeOpencodeTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        string afterFirst = await ReadLogAsync(job.Job.StdoutLog);
        Assert.Contains("which database?", afterFirst, StringComparison.Ordinal);
        Assert.DoesNotContain("ready", afterFirst, StringComparison.Ordinal);

        job.Say("sqlite");
        await LiveJobHarness.FinishedAsync(run);

        string whole = await ReadLogAsync(job.Job.StdoutLog);
        Assert.StartsWith(afterFirst, whole, StringComparison.Ordinal);
        Assert.Contains("ready", whole, StringComparison.Ordinal);
        Assert.Equal(["prt_1_1", "prt_1_2", "prt_2_1", "prt_2_2"], PartIds(whole));
    }

    private static string[] PartIds(string log) =>
        [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("part").GetProperty("id").GetString() ?? "")];

    private static async Task<string> ReadLogAsync(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    // ---- a turn that cannot start (review F6) ---------------------------------------------------------------------------

    [Fact]
    public async Task ALaterTurnThatCannotStartFailsTheJobWithItsReceiptAndItsConversationAsync()
    {
        using LiveJobHarness job = Job(new FakeOpencodeTurn("which database?", BecomesUnrunnable: true), FakeOpencodeTurn.Report("never reached"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("the next turn could not be started", result.Error, StringComparison.Ordinal);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(FakeOpencode.Session, result.SessionId);
        Assert.NotNull(result.Conversation);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal(entries.Length, result.Conversation.Entries);
        Assert.Contains(entries, entry => entry.Kind == ConversationKind.Reply && entry.Text == "sqlite");
        Assert.Contains(entries, entry => entry.Kind == ConversationKind.Notice && entry.Text.Contains("next turn could not be started", StringComparison.Ordinal));
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Equal(1, job.Opencode.Runs);
        Assert.True(File.Exists(job.Job.ResultJson));
    }

    [Fact]
    public async Task ATurnThatExitsNonZeroIsTheJobsFailureWithTheConversationSoFarAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("which database?"), new FakeOpencodeTurn("", ExitCode: 3));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(2, job.Opencode.Runs);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
    }

    // ---- not live: argv unchanged ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ANonLiveOpencodeRunKeepsItsArgvWithNoDashDashAndNoLiveFilesAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Report("plain"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        string[] argv = job.Opencode.Argv(1);
        Assert.Equal(["run", "--standalone", "--agent", "claustrum-builder", "--model", "opencode/fake", "--format", "json", "--auto"], argv[..9]);
        Assert.DoesNotContain("--", argv);
        Assert.StartsWith("do it", argv[9], StringComparison.Ordinal);
        Assert.Null(result.Conversation);
        Assert.False(File.Exists(job.Job.LiveJson));
    }

    [Fact]
    public async Task AResumedNonLiveOpencodeRunNamesTheSessionAndStillHasNoDashDashAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Report("plain"));

        await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, resumeSession: "ses_old"));

        string[] argv = job.Opencode.Argv(1);
        Assert.Equal("ses_old", argv[Array.IndexOf(argv, "--session") + 1]);
        Assert.DoesNotContain("--", argv);
    }

    // ---- notes (the loose end of stage 3) ---------------------------------------------------------------------------------

    [Fact]
    public async Task ANoteInALiveTurnIsNumberedInOrderWithTheTurnsInConversationJsonlAsync()
    {
        using LiveJobHarness job = Job(
            new FakeOpencodeTurn("Looking.\nclaustrum-note: reading the schema\nwhich database?"),
            FakeOpencodeTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("sqlite");

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([1, 2, 3, 4], entries.Select(entry => entry.Seq));
        Assert.Equal((ConversationParty.Role, ConversationKind.Message, "claustrum-note: reading the schema"), (entries[0].From, entries[0].Kind, entries[0].Text));
        Assert.Equal(ConversationKind.Question, entries[1].Kind);
        Assert.Equal(4, result.Conversation?.Entries);
    }

    // ---- blind and no human ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABlindRoleThatEndsATurnWithoutAReportIsToldItCannotAskAndNeverWaitsAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("may I ask?"), FakeOpencodeTurn.Report("findings"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(blind: true));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(2, job.Opencode.Runs);
        Assert.Contains("This role cannot ask questions", job.Opencode.Message(2), StringComparison.Ordinal);
        Assert.DoesNotContain(job.Conversation(), entry => entry.Kind == ConversationKind.Question);
    }

    [Fact]
    public async Task WithReplyTimeoutZeroTheQuestionIsAnsweredAtOnceWithTheNoHumanRuleAsTheNextTurnAsync()
    {
        using LiveJobHarness job = Job(FakeOpencodeTurn.Ask("anyone?"), FakeOpencodeTurn.Report("alone"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 0));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(LiveSession.NoHumanNotice, job.Opencode.Message(2));
        Assert.Contains("stop with status needs_input", job.Opencode.Message(2), StringComparison.Ordinal);
    }
}
