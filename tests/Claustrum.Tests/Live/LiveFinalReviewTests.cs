using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// #127 final review (NOTES.md "Final review of #127"): the cost of a live opencode job that is cancelled between turns (finding 3), the
// `--replay-user-messages` rule of a live claude (finding 4) and the inbox that is a link (finding 5), through the real Runner and LiveSession
// against the fake backends. The echo itself was never seen from a real claude; these cases are the rule's own, as documented.
public sealed class LiveFinalReviewTests
{
    private static async Task<ConversationEntry[]> SettleAsync(LiveJobHarness job, Func<ConversationEntry[], bool> condition, string what)
    {
        await LiveJobHarness.WaitUntilAsync(() => condition(job.Conversation()), what);
        return job.Conversation();
    }

    // ---- 3: a cancelled opencode live job is charged what it spent ---------------------------------------------------------------

    [Fact]
    public async Task ACancelledOpencodeJobWaitingBetweenTurnsIsChargedTheExportedDeltaAsync()
    {
        using LiveJobHarness job = LiveJobHarness.ForOpencode([FakeOpencodeTurn.Ask("which database?", costUsd: 0.10m)], startingCostUsd: 2.00m);
        using CancellationTokenSource cancel = new();
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token, resumeSession: FakeOpencode.Session);
        await job.WaitForQuestionAsync();

        await cancel.CancelAsync();
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(0.10m, result.CostUsd);
        Assert.Equal(2, job.Opencode.Exports);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("post-run", StringComparison.Ordinal));
    }

    // The fallback is today's: the hook says it fell back with a warning, a cancelled job drops that answer and keeps what its own output gave.
    [Fact]
    public async Task ACancelledJobWhoseExportFailsKeepsTheCostItsOwnOutputGaveAndNoNewWarningAsync()
    {
        using LiveJobHarness job = LiveJobHarness.ForOpencode([FakeOpencodeTurn.Ask("which database?", costUsd: 0.10m)]);
        job.Opencode.FailExports();
        using CancellationTokenSource cancel = new();
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token);
        await job.WaitForQuestionAsync();

        await cancel.CancelAsync();
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Null(result.CostUsd);
        Assert.True(job.Opencode.Exports >= 1, "the export was tried");
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("export", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(job.Job.ResultJson));
    }

    // ---- 4: a message claude has not taken yet is the next turn, not a missed reply ----------------------------------------------

    private static FakeClaudeScript Replaying(params FakeClaudeTurn[] turns) => new() { Turns = turns };

    [Fact]
    public async Task AReportlessResultWithAHumanMessageNotYetEchoedStaysRunningAndRecordsAMessageAsync()
    {
        // Turn 1 echoes the brief, which proves echoes work; the human's reply then crosses turn 2's result with no echo before it.
        using LiveJobHarness job = new(Replaying(
            new FakeClaudeTurn("ready?", ReplayTaken: true),
            new FakeClaudeTurn("still on the first thing"),
            FakeClaudeTurn.Report("done") with { ReplayTaken = true }));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        ConversationEntry[] entries = await SettleAsync(job, all => all.Any(entry => entry.Text == "still on the first thing"), "turn 2 to be recorded");

        ConversationEntry turn = Assert.Single(entries, entry => entry.Text == "still on the first thing");
        Assert.Equal((ConversationParty.Role, ConversationKind.Message), (turn.From, turn.Kind));
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(LiveStatus.Running, job.State()?.State);
        Assert.Null(job.State()?.QuestionSeq);
        Assert.False(run.IsCompleted);

        job.Say("go on");
        RunResult result = await LiveJobHarness.FinishedAsync(run);
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(1, job.Conversation().Count(entry => entry.Kind == ConversationKind.Question));
    }

    [Fact]
    public async Task OnceTheMessageIsEchoedAReportlessResultIsAQuestionAsBeforeAsync()
    {
        using LiveJobHarness job = new(Replaying(
            new FakeClaudeTurn("ready?", ReplayTaken: true),
            new FakeClaudeTurn("which database?", ReplayTaken: true),
            FakeClaudeTurn.Report("done")));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.Text == "which database?"), "turn 2");
        LiveState waiting = await job.WaitForQuestionAsync();

        ConversationEntry second = Assert.Single(job.Conversation(), entry => entry.Text == "which database?");
        Assert.Equal(ConversationKind.Question, second.Kind);
        Assert.Equal(second.Seq, waiting.QuestionSeq);
        job.Say("sqlite again");
        Assert.Equal(RunStatus.Success, (await LiveJobHarness.FinishedAsync(run)).Status);
    }

    // Guard (a): until an echo that names a message arrives nothing is tracked, so a claude that never echoes still asks its questions.
    [Fact]
    public async Task AClaudeThatNeverEchoesStillGetsItsQuestionsAskedAsync()
    {
        using LiveJobHarness job = new(Replaying(FakeClaudeTurn.Ask("ready?"), FakeClaudeTurn.Ask("which database?"), FakeClaudeTurn.Report("done")));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("hello");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.Text == "which database?"), "turn 2");
        await job.WaitForQuestionAsync();

        Assert.Equal(ConversationKind.Question, Assert.Single(job.Conversation(), entry => entry.Text == "which database?").Kind);
        job.Say("sqlite");
        Assert.Equal(RunStatus.Success, (await LiveJobHarness.FinishedAsync(run)).Status);
    }

    // Guard (b): the first message's echo never comes and it held back one report-less turn; a later message that IS echoed gets its turn asked, the old one holds nothing more.
    [Fact]
    public async Task AMessageThatIsNeverEchoedHoldsBackOneReportlessTurnAtMostAsync()
    {
        using LiveJobHarness job = new(Replaying(
            new FakeClaudeTurn("ready?", ReplayTaken: true),
            new FakeClaudeTurn("first thought"),
            new FakeClaudeTurn("second thought", ReplayTaken: true),
            FakeClaudeTurn.Report("done")));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("hello");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.Text == "first thought"), "turn 2");
        job.Say("anyone there?");
        await LiveJobHarness.WaitUntilAsync(() => job.Conversation().Any(entry => entry.Text == "second thought"), "turn 3");
        await job.WaitForQuestionAsync();

        Assert.Equal(ConversationKind.Message, Assert.Single(job.Conversation(), entry => entry.Text == "first thought").Kind);
        Assert.Equal(ConversationKind.Question, Assert.Single(job.Conversation(), entry => entry.Text == "second thought").Kind);
        job.Say("yes");
        Assert.Equal(RunStatus.Success, (await LiveJobHarness.FinishedAsync(run)).Status);
    }

    [Fact]
    public async Task EveryHumanMessageOnceEchoesWorkCarriesAUuidAndTheBriefAlwaysDoesAsync()
    {
        using LiveJobHarness job = new(Replaying(new FakeClaudeTurn("ready?", ReplayTaken: true), FakeClaudeTurn.Report("done")));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("sqlite");
        await LiveJobHarness.FinishedAsync(run);

        string[] input = job.Input();
        Assert.Equal(2, input.Length);
        Assert.All(input, line => Assert.Contains("\"uuid\":\"", line, StringComparison.Ordinal));
        Assert.Contains("--replay-user-messages", File.ReadAllLines(job.ArgvLog));
    }

    // ---- 5: an inbox that is a link is refused ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInboxThatIsALinkIsRefusedWithOneNoticeAndNothingOutsideIsReadOrDeletedAsync()
    {
        using LiveJobHarness job = new(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("ready?"), FakeClaudeTurn.Report("fine")] });
        using CancellationTokenSource cancel = new();
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token);
        await job.WaitForQuestionAsync();

        string outside = Directory.CreateTempSubdirectory("claustrum-inbox-outside-").FullName;
        try
        {
            string message = Path.Combine(outside, "1-message.json");
            string other = Path.Combine(outside, "2-other.json");
            string keep = Path.Combine(outside, "keep.txt");
            File.WriteAllText(message, /*lang=json,strict*/ """{"at":"2026-10-16T00:00:00+00:00","text":"from outside"}""");
            File.WriteAllText(other, "{}");
            File.WriteAllText(keep, "keep");
            Directory.Delete(job.InboxDirectory, recursive: true);
            TestLinks.CreateDirectoryLink(job.InboxDirectory, outside);

            ConversationEntry[] entries = await SettleAsync(job, all => all.Any(entry => entry.Kind == ConversationKind.Notice && entry.Text.Contains("inbox directory", StringComparison.Ordinal)), "the refusal notice");
            await Task.Delay(700, TestContext.Current.CancellationToken);

            Assert.Single(job.Conversation(), entry => entry.Kind == ConversationKind.Notice && entry.Text.Contains("The inbox directory is a link", StringComparison.Ordinal));
            Assert.DoesNotContain(job.Conversation(), entry => entry.Text == "from outside");
            Assert.True(File.Exists(message) && File.Exists(other) && File.Exists(keep), "nothing outside the job was deleted");
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.Single(job.Input());
            Assert.Equal(LiveStatus.WaitingForInput, job.State()?.State);
            Assert.NotEmpty(entries);

            await cancel.CancelAsync();
            RunResult result = await LiveJobHarness.FinishedAsync(run);
            Assert.Equal(RunStatus.Cancelled, result.Status);
            Assert.True(File.Exists(message), "a cancelled session does not sweep a linked inbox either");
        }
        finally
        {
            if (new DirectoryInfo(job.InboxDirectory).LinkTarget is not null)
                Directory.Delete(job.InboxDirectory);

            Directory.Delete(outside, recursive: true);
        }
    }
}
