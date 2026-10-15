using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// #124 stage 1, the engine: Runner + LiveSession + ProcessRunner against a conversing fake claude (FakeClaude.Turns),
// which reads one stream-json user message per stdin line, answers with an `assistant` and a `result`, stays alive between
// turns and exits 0 when stdin closes. The inbox is written the way `jobs say` writes it.
public sealed class LiveEngineTests
{
    private static FakeClaudeScript Conversation(params FakeClaudeTurn[] turns) => new() { Turns = turns };

    [Fact]
    public async Task AQuestionWaitsForTheInboxReplyWhichIsRecordedAsAReplyAndSentAsTheNextTurnAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("which database?"), FakeClaudeTurn.Report("used it", "ready", echoReply: true)));
        Task<RunResult> run = job.StartAsync();

        LiveState waiting = await job.WaitForQuestionAsync();
        ConversationEntry question = Assert.Single(job.Conversation());
        Assert.Equal(question.Seq, waiting.QuestionSeq);
        Assert.Equal((ConversationParty.Role, ConversationKind.Question, "which database?"), (question.From, question.Kind, question.Text));
        Assert.False(run.IsCompleted);

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Question, ConversationKind.Reply, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal([1, 2, 3], entries.Select(entry => entry.Seq));
        Assert.Equal("sqlite", entries[1].Text);
        Assert.Equal(ConversationParty.Human, entries[1].From);
        Assert.Contains("ready you said: sqlite", entries[2].Text, StringComparison.Ordinal);
        Assert.Equal(3, result.Conversation?.Entries);
        Assert.Equal(job.Job.ConversationJsonl, result.Conversation?.Path);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Null(job.State()?.QuestionSeq);
        Assert.Empty(Directory.GetFiles(job.InboxDirectory));
    }

    [Fact]
    public async Task TheBriefGoesInAsTheFirstUserTurnAndEachReplyIsTheNextOneOnTheSameStdinAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("q?"), FakeClaudeTurn.Report("s")));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("an answer");
        await LiveJobHarness.FinishedAsync(run);

        string[] argv = File.ReadAllLines(job.ArgvLog);
        Assert.Equal("stream-json", argv[Array.IndexOf(argv, "--input-format") + 1]);
        string[] input = job.Input();
        Assert.Equal(2, input.Length);
        Assert.StartsWith("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"do it", input[0], StringComparison.Ordinal);
        Assert.Contains("This is a live session with the human who started you", input[0], StringComparison.Ordinal);
        Assert.EndsWith("\"text\":\"an answer\"}]}}", input[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportOnTheFirstTurnNeverWaitsAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Report("finished", "no questions")));
        Task<RunResult> run = job.StartAsync();

        List<LiveStatus> seen = [];
        while (!run.IsCompleted)
        {
            if (job.State() is { } state)
                seen.Add(state.State);

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        RunResult result = await run;
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.DoesNotContain(LiveStatus.WaitingForInput, seen);
        ConversationEntry only = Assert.Single(job.Conversation());
        Assert.Equal(ConversationKind.Message, only.Kind);
        Assert.Single(job.Input());
    }

    [Fact]
    public async Task ARoleThatIsNotLiveWithoutAReportBlockOrWithLiveOffRunsAsItDidBeforeAsync()
    {
        using LiveJobHarness noReport = new(new FakeClaudeScript { Summary = "x" });
        RunResult withoutReport = await LiveJobHarness.FinishedAsync(noReport.StartAsync(hasReport: false));
        Assert.Null(withoutReport.Conversation);
        Assert.False(File.Exists(noReport.Job.LiveJson));
        Assert.DoesNotContain("--input-format", File.ReadAllLines(noReport.ArgvLog));

        using LiveJobHarness off = new(new FakeClaudeScript { Summary = "x" });
        RunResult result = await LiveJobHarness.FinishedAsync(off.StartAsync(live: false));
        Assert.Null(result.Conversation);
        Assert.False(File.Exists(off.Job.LiveJson));
        Assert.False(Directory.Exists(off.InboxDirectory));
    }

    [Fact]
    public async Task ANonLiveRunKeepsItsArgvAndItsRawBriefOnStdinAsync()
    {
        string stdinLog = Path.Combine(Path.GetTempPath(), $"claustrum-live-stdin-{Guid.NewGuid():N}.log");
        try
        {
            using LiveJobHarness job = new(new FakeClaudeScript { Summary = "x", StdinLog = stdinLog });
            RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

            Assert.Equal(RunStatus.Success, result.Status);
            Assert.DoesNotContain(File.ReadAllLines(job.ArgvLog), line => line.StartsWith("--input-format", StringComparison.Ordinal));
            Assert.Equal(
                "do it\n\n---\nFinish your reply with the mandatory ```claustrum-report fenced JSON block from your system prompt; a reply without it is rejected.",
                File.ReadAllText(stdinLog));
        }
        finally
        {
            File.Delete(stdinLog);
        }
    }

    [Fact]
    public async Task ARepliesWindowThatPassesSendsTheNoAnswerNoticeAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("decided alone")));
        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 1));

        Assert.Equal(RunStatus.Success, result.Status);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Question, ConversationKind.Notice, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.StartsWith("No answer arrived within 1 second.", entries[1].Text, StringComparison.Ordinal);
        Assert.Contains("open_decisions", job.Input()[1], StringComparison.Ordinal);
        Assert.Contains("No answer arrived within 1 second", job.Input()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyTimeoutOfZeroTellsTheRoleAtOnceAndNeverPublishesAWaitAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("anyone?"), FakeClaudeTurn.Report("decided alone")));
        Task<RunResult> run = job.StartAsync(replyTimeoutSeconds: 0);

        List<LiveStatus> seen = [];
        while (!run.IsCompleted)
        {
            if (job.State() is { } state)
                seen.Add(state.State);

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        RunResult result = await run;
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.DoesNotContain(LiveStatus.WaitingForInput, seen);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Question, ConversationKind.Notice, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal(LiveSession.NoHumanNotice, entries[1].Text);
        Assert.StartsWith("No human is available to answer now.", entries[1].Text, StringComparison.Ordinal);
        Assert.Contains("stop with status needs_input", entries[1].Text, StringComparison.Ordinal);
        Assert.Contains("No human is available to answer now.", job.Input()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClockStandsStillWhileTheJobWaitsForAReplyAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("take your time?"), FakeClaudeTurn.Report("answered")));
        Task<RunResult> run = job.StartAsync(timeout: TimeSpan.FromSeconds(3));

        await job.WaitForQuestionAsync();
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted, "the job was stopped while it waited for its human");
        job.Say("yes");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
    }

    [Fact]
    public async Task TheEightyFivePercentWarningIsSentOnceAndMidTurnAsync()
    {
        // The turn blocks reading one more stdin line, which is the warning: the job is stopped at 10 s, the warning goes at 8.5 s.
        using LiveJobHarness job = new(Conversation(new FakeClaudeTurn("wrapping up", "ok", ReadMidTurn: 1)));
        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(timeout: TimeSpan.FromSeconds(10)));

        Assert.Equal(RunStatus.Success, result.Status);
        string[] warnings = [.. job.Input().Where(line => line.Contains("before this job is stopped", StringComparison.Ordinal))];
        Assert.Single(warnings);
        Assert.Contains("write your claustrum-report now", warnings[0], StringComparison.Ordinal);
        Assert.Single(job.Conversation(), entry => entry.Kind == ConversationKind.Notice);
    }

    [Fact]
    public async Task AMessageToARunningRoleIsWrittenAtOnceAndRecordedAsAMessageAsync()
    {
        using LiveJobHarness job = new(Conversation(new FakeClaudeTurn("steered", "ok", EchoReply: true, ReadMidTurn: 1)));
        Task<RunResult> run = job.StartAsync();
        await LiveJobHarness.WaitUntilAsync(() => job.State() is not null, "live.json");

        job.Say("go left");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal((ConversationParty.Human, "go left"), (entries[0].From, entries[0].Text));
        Assert.Contains("steered you said: go left", result.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlindRolesQuestionGetsTheTellOnlyNoticeAndNeverWaitsAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("is this wrong?"), FakeClaudeTurn.Report("noted")));
        Task<RunResult> run = job.StartAsync(blind: true);

        List<LiveStatus> seen = [];
        while (!run.IsCompleted)
        {
            if (job.State() is { } state)
                seen.Add(state.State);

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        RunResult result = await run;
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.DoesNotContain(LiveStatus.WaitingForInput, seen);
        ConversationEntry[] entries = job.Conversation();
        Assert.Equal([ConversationKind.Message, ConversationKind.Notice, ConversationKind.Message], entries.Select(entry => entry.Kind));
        Assert.Equal(LiveSession.BlindNotice, entries[1].Text);
        Assert.Contains("This role cannot ask questions", job.Input()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHumanMessageToABlindRoleIsDroppedWithANoticeAndNeverReachesTheRoleAsync()
    {
        using LiveJobHarness job = new(Conversation(new FakeClaudeTurn("reviewed", "ok", SleepSeconds: 3)));
        Task<RunResult> run = job.StartAsync(blind: true);
        await LiveJobHarness.WaitUntilAsync(() => job.State() is not null, "live.json");

        job.Say("the rationale you were not meant to see");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Contains(job.Conversation(), entry => entry is { Kind: ConversationKind.Notice } && entry.Text == LiveSession.BlindInputNotice);
        Assert.DoesNotContain(job.Conversation(), entry => entry.From == ConversationParty.Human);
        string[] input = job.Input();
        Assert.Single(input);
        Assert.DoesNotContain("rationale", input[0], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(job.InboxDirectory));
    }

    [Fact]
    public async Task ARoleThatNeverWritesTheReportEndsAfterOneAutomaticAnswerAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("still thinking")));
        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 0));

        Assert.Equal(ReportStatus.Missing, result.ReportStatus);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Equal(2, job.Input().Length);
    }

    [Fact]
    public async Task CostAndUsageComeFromTheLastResultAndTheFinalMessageIsTheReportTurnAsync()
    {
        using LiveJobHarness job = new(Conversation(
            new FakeClaudeTurn("first words", TotalCostUsd: 0.2037m),
            new FakeClaudeTurn("last words", "done", TotalCostUsd: 0.2278m)));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("go on");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(0.2278m, result.CostUsd);
        Assert.Equal(20, result.Usage?.InputTokens);
        Assert.Equal(10, result.Usage?.OutputTokens);
        Assert.StartsWith("last words", result.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("\"summary\":\"done\"", result.Report?.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportThatCrossesTheNoAnswerNoticeStillEndsCompletedWhenTheProcessLingersPastTheTimeoutAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("anyone?"), new FakeClaudeTurn("decided", "ok", LingerSeconds: 10)));
        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 1, timeout: TimeSpan.FromSeconds(3)));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ATimeoutBeforeAnyReportIsStillATimeoutAsync()
    {
        using LiveJobHarness job = new(Conversation(new FakeClaudeTurn("slow", "ok", SleepSeconds: 10)));
        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(timeout: TimeSpan.FromSeconds(2)));

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.NotNull(result.Conversation);
    }

    [Fact]
    public async Task LiveJsonNamesTheOwnerProcessAndItsStartTimeAsync()
    {
        using LiveJobHarness job = new(Conversation(FakeClaudeTurn.Ask("q?"), FakeClaudeTurn.Report("s")));
        Task<RunResult> run = job.StartAsync();
        LiveState waiting = await job.WaitForQuestionAsync();
        job.Say("a");
        await LiveJobHarness.FinishedAsync(run);

        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(Environment.ProcessId, waiting.OwnerPid);
        Assert.NotNull(waiting.OwnerStartedAt);
        Assert.True((waiting.OwnerStartedAt.Value - self.StartTime.ToUniversalTime()).Duration() < TimeSpan.FromSeconds(5));
        Assert.Equal(60, waiting.ReplyTimeoutSeconds);
        Assert.True(waiting.IsOwnerAlive());
    }
}
