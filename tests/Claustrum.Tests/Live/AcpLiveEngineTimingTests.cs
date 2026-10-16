using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;
using static Claustrum.Tests.Testing.AcpEngine;

namespace Claustrum.Tests.Live;

// #130, the engine on `acp-opencode`, the cases that take seconds because the agent is made to be slow: a message sent mid-turn waits for the turn's end, the clock stands still
// while a human is asked, an agent that lingers past its answer, a timeout and a cancel mid-turn. Kept apart from AcpLiveEngineTests so the two run side by side.
public sealed class AcpLiveEngineTimingTests
{
    private static LiveJobHarness Job(params FakeAcpTurn[] turns) => LiveJobHarness.ForAcp(turns);

    [Fact]
    public async Task AMessageThatArrivesMidTurnWaitsForTheTurnsEndAndAnswersTheQuestionItEndsWithAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?", sleepSeconds: 4), FakeAcpTurn.Report("used it"));
        Task<RunResult> run = job.StartAsync();
        await WaitForPromptsAsync(job, 1);

        job.Say("sqlite please");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.From == ConversationParty.Human), "the message to be recorded");

        // Recorded at once, but the agent is still in its turn (opencode would refuse a second prompt with -32603): none was sent.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(0, job.Acp.AnsweredPrompts);
        Assert.Equal(1, ClientMethods(job).Count(method => method == "session/prompt"));

        List<LiveStatus> seen = [];
        while (!run.IsCompleted)
        {
            if (job.State() is { } state)
                seen.Add(state.State);

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        RunResult result = await run;

        // Sent as the next prompt once the turn ended, and never published as an open wait.
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.DoesNotContain(LiveStatus.WaitingForInput, seen);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Question, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal((ConversationParty.Human, "sqlite please"), (entries[0].From, entries[0].Text));
        Assert.Equal(2, job.Acp.Prompts);
        Assert.Equal("sqlite please", job.Acp.PromptText(2));
        Assert.DoesNotContain(entries, entry => entry.Text == LiveSession.UndeliveredNotice);
    }

    [Fact]
    public async Task AMessageStillQueuedWhenTheTurnEndsWithTheReportGetsTheUndeliveredNoticeAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("finished", sleepSeconds: 3));
        Task<RunResult> run = job.StartAsync();
        await WaitForPromptsAsync(job, 1);

        job.Say("too late");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.From == ConversationParty.Human), "the message to be recorded");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(1, job.Acp.Prompts);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Message, ConversationKind.Notice], entries.Select(entry => entry.Kind));
        Assert.Equal(LiveSession.UndeliveredNotice, entries[^1].Text);
        Assert.Equal(ConversationParty.Claustrum, entries[^1].From);
    }

    [Fact]
    public async Task SeveralMessagesQueuedDuringATurnGoOutAsOneNextPromptJoinedByABlankLineAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("anything else?", sleepSeconds: 3), FakeAcpTurn.Report("ok"));
        Task<RunResult> run = job.StartAsync();
        await WaitForPromptsAsync(job, 1);

        job.Say("first");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Count(entry => entry.From == ConversationParty.Human) == 1, "the first message");
        job.Say("second");
        await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(2, job.Acp.Prompts);
        Assert.Equal("first\n\nsecond", job.Acp.PromptText(2));
    }

    [Fact]
    public async Task TheClockStandsStillWhileTheJobWaitsForAReplyAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("take your time?"), FakeAcpTurn.Report("answered"));
        Task<RunResult> run = job.StartAsync(timeout: TimeSpan.FromSeconds(3));

        await job.WaitForQuestionAsync();
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted, "the job was stopped while it waited for its human");
        job.Say("yes");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
    }

    // An agent that does not exit on stdin EOF (opencode does, in 30-40 ms) holds the process past the report; the report was delivered, so the stop that follows is housekeeping.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAgentThatLingersAfterItsAnswerDoesNotTurnASuccessIntoATimeoutAsync(bool live)
    {
        using LiveJobHarness job = LiveJobHarness.ForAcp([FakeAcpTurn.Report("done", costUsd: 0.04m)], lingerAfterEofSeconds: 20);

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: live, timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal(0.04m, result.CostUsd);
        await AssertNoOrphanAsync(job);
    }

    private static FakeAcpTurn Stalled() => new("working on it", StallSeconds: 120, Tools: [new FakeAcpTool("shell", "sleep 120", Status: "in_progress")]);

    [Fact]
    public async Task ATimeoutMidTurnIsATimeoutWithNoOrphanAndTheCostComesFromTheExportAsync()
    {
        using LiveJobHarness job = Job(Stalled());

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.Equal(0.01m, result.CostUsd);
        Assert.Equal(1, job.Acp.Exports);
        await AssertNoOrphanAsync(job);
    }

    [Fact]
    public async Task ACancelMidTurnIsCancelledWithNoOrphanAsync()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using LiveJobHarness job = Job(Stalled());
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token);
        await LiveJobHarness.WaitUntilAsync(() => job.Acp.NapPid is not null, "the agent to be mid-turn");

        await cancel.CancelAsync();
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        await AssertNoOrphanAsync(job);
    }

    [Fact]
    public async Task AnAgentThatHangsInItsHandshakeIsStoppedByTheTimeoutWithNoOrphanAndNothingPromptedAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.StallOn("initialize", 120);

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.Equal(0, job.Acp.Prompts);
        Assert.Equal(["initialize"], ClientMethods(job));
        await AssertNoOrphanAsync(job);
    }

    [Fact]
    public async Task ACancelWhileTheJobWaitsForAHumanIsCancelledWithNoOrphanAsync()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"));
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token);
        await job.WaitForQuestionAsync();

        await cancel.CancelAsync();
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        await AssertNoOrphanAsync(job);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
    }
}
