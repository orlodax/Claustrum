using System.Text.Json;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;
using static Claustrum.Tests.Testing.AcpEngine;

namespace Claustrum.Tests.Live;

// #130, the engine on `acp-opencode`: Runner + AcpSession + LiveSession + ProcessRunner against a fake `opencode acp` (FakeAcpAgent) that holds the whole
// session in one process: each turn is a `session/prompt` on its stdin and its answer the turn's end. The inbox is written the way `jobs say` writes it. The
// per-turn rules are the opencode ones (OpencodeLiveEngineTests): a message sent mid-turn waits for the turn's end, because opencode refuses a second prompt.
public sealed class AcpLiveEngineTests
{
    private static LiveJobHarness Job(params FakeAcpTurn[] turns) => LiveJobHarness.ForAcp(turns);

    private static async Task<string> ReadLogAsync(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    // ---- the turns ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATurnThatAsksWaitsAndTheReplyIsSentAsTheNextPromptOfTheSameProcessAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();

        LiveState waiting = await job.WaitForQuestionAsync();
        ConversationEntry question = Assert.Single(job.Conversation());
        Assert.Equal(question.Seq, waiting.QuestionSeq);
        Assert.Equal("which database?", question.Text);
        Assert.False(run.IsCompleted);
        Assert.Equal(1, job.Acp.Prompts);

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal(1, job.Acp.Runs);
        Assert.Equal(2, job.Acp.Prompts);
        Assert.Equal([ConversationKind.Question, ConversationKind.Reply, ConversationKind.Message], job.Conversation().Select(entry => entry.Kind));
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.StartsWith("do it", job.Acp.PromptText(1), StringComparison.Ordinal);
        Assert.Equal("sqlite", job.Acp.PromptText(2));
        Assert.Equal(FakeAcpAgent.Session, result.SessionId);
        Assert.Equal(0, result.ExitCode);

        // The receipt's message is the last turn's, not both turns' text run together.
        Assert.Contains("ready", result.FinalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("which database?", result.FinalMessage, StringComparison.Ordinal);

        // Usage is each prompt's own, summed: two answers of 10 in and 5 out.
        Assert.Equal(new Usage(20, 10, null, null), result.Usage);
    }

    [Fact]
    public async Task SeveralQuestionsInARowKeepOneProcessSumTheUsageAndBillTheExportedTotalAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("first?", costUsd: 0.01m), FakeAcpTurn.Ask("second?", costUsd: 0.02m), FakeAcpTurn.Report("done", "third", costUsd: 0.04m));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("one");
        await LiveJobHarness.WaitUntilAsync(() => job.Acp.Prompts == 2 && job.Conversation().Count(entry => entry.Kind == ConversationKind.Question) == 2, "the second question");
        await LiveJobHarness.WaitUntilAsync(() => job.State() is { State: LiveStatus.WaitingForInput, QuestionSeq: { } seq } && seq == job.Conversation().Last(entry => entry.Kind == ConversationKind.Question).Seq, "the job to wait on the second question");
        const string Second = "two\nlines, \"quoted\", \\ and \u00e9 \ud83d\ude00";
        job.Say(Second);

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(1, job.Acp.Runs);
        Assert.Equal(3, job.Acp.Prompts);
        Assert.Equal("one", job.Acp.PromptText(2));
        Assert.Equal(Second, job.Acp.PromptText(3));
        Assert.Equal(["init-1", "new-2", "config-3", "config-4", "prompt-5", "prompt-6", "prompt-7"], ClientFrames(job).Select(IdOf));
        Assert.Equal(0.07m, result.CostUsd);
        Assert.Equal(1, job.Acp.Exports);
        Assert.Equal(new Usage(30, 15, null, null), result.Usage);
        Assert.Contains("third", result.FinalMessage, StringComparison.Ordinal);
        Assert.Equal([ConversationKind.Question, ConversationKind.Reply, ConversationKind.Question, ConversationKind.Reply, ConversationKind.Message], job.Conversation().Select(entry => entry.Kind));
    }

    // opencode writes UTF-8 as itself, not as \uXXXX escapes: a reply with accents, CJK, an emoji and curly quotes crosses the pipe whole, even when a chunk boundary falls inside it.
    [Fact]
    public async Task AReplyInRawUtf8CrossesThePipeIntactAsync()
    {
        const string Text = "h\u00e9llo \u65e5\u672c\u8a9e \ud83d\ude00 \u201cquotes\u201d \u2013 done";
        using LiveJobHarness job = Job(new FakeAcpTurn(Text, Summary: "utf8"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.StartsWith(Text, result.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("\u65e5\u672c\u8a9e", await ReadLogAsync(job.Job.StdoutLog), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryFrameTheClientSentIsInTheClientLogInOrderWithItsOwnIdsAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("sqlite");
        await LiveJobHarness.FinishedAsync(run);

        string[] frames = ClientFrames(job);

        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt", "session/prompt"], frames.Select(MethodOf));
        Assert.Equal(["init-1", "new-2", "config-3", "config-4", "prompt-5", "prompt-6"], frames.Select(IdOf));
        Assert.Equal(job.Acp.Received, frames);
        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt", "session/prompt"], job.Acp.Methods);
        Assert.Equal(["model=opencode/fake", "mode=claustrum-builder"], job.Acp.ConfigOptions);
    }

    [Fact]
    public async Task AReportEndsTheSessionStdinClosesAndTheAgentExitsCleanAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("done"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, job.Acp.AnsweredPrompts);
        Assert.False(FakeAcpAgent.IsRunning(job.Acp.Pid(1)));
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt"], ClientMethods(job));
    }

    // ---- blind and no human ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABlindRoleThatEndsATurnWithoutAReportIsToldItCannotAskAndNeverWaitsAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("may I ask?"), FakeAcpTurn.Report("findings"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(blind: true));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(2, job.Acp.Prompts);
        Assert.Contains("This role cannot ask questions", job.Acp.PromptText(2), StringComparison.Ordinal);
        Assert.DoesNotContain(job.Conversation(), entry => entry.Kind == ConversationKind.Question);
    }

    [Fact]
    public async Task WithReplyTimeoutZeroTheQuestionIsAnsweredAtOnceWithTheNoHumanRuleAsTheNextPromptAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("anyone?"), FakeAcpTurn.Report("alone"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 0));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(LiveSession.NoHumanNotice, job.Acp.PromptText(2));
        Assert.Contains("stop with status needs_input", job.Acp.PromptText(2), StringComparison.Ordinal);
    }

    // ---- a turn that goes wrong -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATurnAnsweredWithAJsonRpcErrorEndsTheSessionAsAFailureWithTheProvidersWordsAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"), new FakeAcpTurn("", Error: new FakeAcpError(-32603, "the provider is down", ErrorName: "provider.network"), Priced: false));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("the provider is down", result.Error, StringComparison.Ordinal);
        Assert.Equal(ErrorKind.ProviderTransport, result.ErrorKind);
        Assert.Equal(2, job.Acp.Prompts);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Contains(job.Conversation(), entry => entry.Kind == ConversationKind.Reply && entry.Text == "sqlite");
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    [InlineData("max_turn_requests")]
    public async Task AStopReasonThatCutTheTurnEndsTheSessionAsAFailureAsync(string stopReason)
    {
        using LiveJobHarness job = Job(new FakeAcpTurn("I will not do that", StopReason: stopReason));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains($"the agent stopped: {stopReason}", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, job.Acp.Prompts);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
    }

    // A refused permission ends a turn there with `cancelled`: the session lives on, and a report-less turn is the role's question to its human.
    [Fact]
    public async Task ACancelledTurnIsNotAnErrorItIsAQuestionTheSessionGoesOnFromAsync()
    {
        using LiveJobHarness job = Job(new FakeAcpTurn("May I run git push?", StopReason: "cancelled"), FakeAcpTurn.Report("did without"));
        Task<RunResult> run = job.StartAsync();

        await job.WaitForQuestionAsync();
        Assert.False(run.IsCompleted);
        job.Say("no, do without");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(2, job.Acp.Prompts);
    }

    // Nothing to answer: the session ends there with a notice saying why, and no second prompt is sent. (The receipt's status is Parse's: a clean answer
    // with no text is not an error to it, as on the native opencode backend, whose live protocol ends the session the same way.)
    [Fact]
    public async Task ATurnAnsweredWithNoTextAtAllEndsTheSessionWithANoticeNamingWhyAsync()
    {
        using LiveJobHarness job = Job(new FakeAcpTurn("", Chunks: []), FakeAcpTurn.Report("never reached"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        ConversationEntry notice = Assert.Single(job.Conversation());
        Assert.Equal((ConversationParty.Claustrum, ConversationKind.Notice), (notice.From, notice.Kind));
        Assert.Equal("The role's session ended with an error: the agent ended the turn without any text", notice.Text);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.Equal(1, job.Acp.Prompts);
        Assert.Equal("", result.FinalMessage);
        Assert.Equal(ReportStatus.Missing, result.ReportStatus);
    }

    [Fact]
    public async Task AnAgentThatDiesOnALaterPromptIsTheJobsFailureWithTheConversationSoFarAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"), new FakeAcpTurn("", ExitCode: 3));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(2, job.Acp.Prompts);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
        Assert.NotNull(result.Conversation);
        Assert.Equal(job.Conversation().Length, result.Conversation.Entries);
    }

    // ---- the logs ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StdoutLogGrowsAcrossTurnsAsOneContinuousFileOfTheAgentsFramesAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        string afterFirst = await ReadLogAsync(job.Job.StdoutLog);
        Assert.Contains("which d", afterFirst, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"prompt-5\"", afterFirst, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt-6", afterFirst, StringComparison.Ordinal);

        job.Say("sqlite");
        await LiveJobHarness.FinishedAsync(run);

        string whole = await ReadLogAsync(job.Job.StdoutLog);
        Assert.StartsWith(afterFirst, whole, StringComparison.Ordinal);
        string[] answers = [.. whole.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.Contains("\"stopReason\"", StringComparison.Ordinal)).Select(IdOf)];
        Assert.Equal(["prompt-5", "prompt-6"], answers);
    }

    // ---- cost ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshLiveSessionIsBilledTheSessionsExportedTotalAcrossAllItsTurnsAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("q?", costUsd: 0.10m), FakeAcpTurn.Report("s", costUsd: 0.05m));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("a");

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(0.15m, result.CostUsd);
        Assert.Equal(1, job.Acp.Exports);
        Assert.Equal(["session export --standalone ses_fake"], job.Acp.ExportCalls);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task AResumedLiveSessionLoadsTheSessionAndIsBilledOnlyWhatItAddedTheExportAfterMinusBeforeAsync()
    {
        using LiveJobHarness job = LiveJobHarness.ForAcp([FakeAcpTurn.Ask("q?", costUsd: 0.10m), FakeAcpTurn.Report("s", costUsd: 0.05m)], startingCostUsd: 2.00m);
        Task<RunResult> run = job.StartAsync(resumeSession: FakeAcpAgent.Session);
        await job.WaitForQuestionAsync();
        job.Say("a");

        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(0.15m, result.CostUsd);
        Assert.Equal(2, job.Acp.Exports);
        Assert.All(job.Acp.ExportCalls, call => Assert.Equal("session export --standalone ses_fake", call));
        Assert.Equal(["initialize", "session/load", "session/set_config_option", "session/set_config_option", "session/prompt", "session/prompt"], job.Acp.Methods);
        Assert.Equal(["init-1", "load-2", "config-3", "config-4", "prompt-5", "prompt-6"], ClientFrames(job).Select(IdOf));

        // What the load replayed is the session's history, never this run's receipt.
        Assert.DoesNotContain("REPLAYED-HISTORY", result.FinalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(job.Conversation(), entry => entry.Text.Contains("REPLAYED-HISTORY", StringComparison.Ordinal));
        Assert.Equal(FakeAcpAgent.Session, result.SessionId);
    }

    [Fact]
    public async Task WhenTheExportFailsASettledStreamsFigureStandsWithAWarningNamingTheBackendAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("done", costUsd: 0.07m));
        job.Acp.FailExports("database is locked");

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.07m, result.CostUsd);
        Assert.Contains("acp-opencode: session total unavailable (database is locked): cost is the stream's last usage_update", result.Warnings);
    }

    [Fact]
    public async Task WhenTheExportFailsAndTheStreamNeverPricedTheTurnTheCostIsUnknownAndTheCapIsChargedAsync()
    {
        using LiveJobHarness job = Job(new FakeAcpTurn("all done", Summary: "done", Priced: false));
        job.Acp.FailExports("database is locked");

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Contains("acp-opencode: session total unavailable (database is locked): cost unknown, charged the cap", result.Warnings);
    }

    // ---- not live: the same process, one prompt --------------------------------------------------------------------------------------

    [Fact]
    public async Task ANonLiveRunSendsTheHandshakeAndOnePromptThenClosesStdinAndKeepsNoLiveFilesAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("plain", costUsd: 0.02m));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(0.02m, result.CostUsd);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["acp"], job.Acp.Argv(1));
        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt"], ClientMethods(job));
        Assert.StartsWith("do it", job.Acp.PromptText(1), StringComparison.Ordinal);
        Assert.Null(result.Conversation);
        Assert.False(File.Exists(job.Job.LiveJson));
        Assert.Equal(1, job.Acp.Exports);
    }

    [Fact]
    public async Task TheAgentIsLaunchedWithTheRolesAgentInItsConfigTheFilewatcherOffAndTheRunsDirectoryAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("plain"));

        await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, deny: ["git push"]));

        using JsonDocument config = JsonDocument.Parse(job.Acp.ConfigContent(1));
        JsonElement agent = config.RootElement.GetProperty("agent").GetProperty("claustrum-builder");
        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Contains("git push", agent.GetProperty("permission").GetProperty("bash").EnumerateObject().Select(property => property.Name));
        Assert.Equal("1", job.Acp.Filewatcher(1));
        Assert.Equal(Path.GetFullPath(job.Cwd), Path.GetFullPath(job.Acp.Cwd(1)));
    }

    [Fact]
    public async Task AVariantInTheModelIdIsSetAsTheEffortBetweenTheModelAndTheModeAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("plain"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, modelId: "opencode/fake#high"));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(["model=opencode/fake", "effort=high", "mode=claustrum-builder"], job.Acp.ConfigOptions);
        Assert.Equal(["init-1", "new-2", "config-3", "config-4", "config-5", "prompt-6"], ClientFrames(job).Select(IdOf));
    }

    // ---- a refusal in the handshake -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AModelTheAgentDoesNotKnowIsRefusedAtTheConfigOptionAsModelNotFoundAtNoCostAndNothingIsPromptedAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.Refuse("session/set_config_option", new FakeAcpError(-32602, "Invalid params: model not found: opencode/fake", ModelId: "opencode/fake"), configId: "model");

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(ErrorKind.ModelNotFound, result.ErrorKind);
        Assert.Equal("Invalid params: model not found: opencode/fake", result.ErrorDetail);
        Assert.Equal(0m, result.CostUsd);
        Assert.True(result.BeforeAnyWork);
        Assert.Equal(0, job.Acp.Prompts);
        Assert.Equal(0, job.Acp.Exports);
        Assert.DoesNotContain("session/prompt", ClientMethods(job));
        Assert.Equal(["model=opencode/fake"], job.Acp.ConfigOptions);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ABadVariantIsRefusedAtTheEffortOptionAsOtherNotAsModelNotFoundAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.Refuse("session/set_config_option", new FakeAcpError(-32602, "Invalid params: effort not found: bogus"), configId: "effort");

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(modelId: "opencode/fake#bogus"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(ErrorKind.Other, result.ErrorKind);
        Assert.Equal(0m, result.CostUsd);
        Assert.True(result.BeforeAnyWork);
        Assert.Equal(["model=opencode/fake", "effort=bogus"], job.Acp.ConfigOptions);
        Assert.Equal(0, job.Acp.Prompts);
    }

    [Theory]
    [InlineData("initialize", 1)]
    [InlineData("session/new", 2)]
    public async Task AnyRefusalEarlierInTheHandshakeEndsTheRunTheSameWayAsync(string method, int framesSent)
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.Refuse(method, new FakeAcpError(-32603, $"{method} failed"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(ErrorKind.Other, result.ErrorKind);
        Assert.Equal(0m, result.CostUsd);
        Assert.Contains($"{method} failed", result.Error, StringComparison.Ordinal);
        Assert.Equal(framesSent, ClientFrames(job).Length);
        Assert.Equal(0, job.Acp.Prompts);
    }

    [Fact]
    public async Task ASessionTheAgentCannotLoadIsRefusedBeforeAnyPromptAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.Refuse("session/load", new FakeAcpError(-32602, "Session not found"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(resumeSession: "ses_gone"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("Session not found", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, job.Acp.Prompts);
        Assert.Equal(["init-1", "load-2"], ClientFrames(job).Select(IdOf));

        // Only the baseline read before the run (it could not know the load would fail); nothing was prompted, so nothing is read after it.
        Assert.Equal(1, job.Acp.Exports);
        Assert.Equal(0m, result.CostUsd);
    }

    // ---- an agent that misbehaves ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgentThatDiesBeforeAnsweringInitializeIsAFailedJobWithItsExitCodeAndStderrNotAHangAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.CrashOn("initialize", 5);

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync());

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal("crashed", result.FinalMessage);
        Assert.Equal(0, job.Acp.Prompts);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
    }

    [Fact]
    public async Task AnAgentThatDiesAfterOpeningItsSessionStillLeavesTheSessionIdOnTheReceiptAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("never reached"));
        job.Acp.CrashOn("session/set_config_option", 4);

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(FakeAcpAgent.Session, result.SessionId);
        Assert.Equal(0, job.Acp.Prompts);
    }

    // The agent's stdout is the protocol; whatever else lands on it is skipped, by the driver and by Parse alike.
    [Fact]
    public async Task LinesThatAreNotJsonRpcOnTheAgentsStdoutAreIgnoredAndKeptInTheLogAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Report("fine", "all good"));
        job.Acp.PrintBeforeInitialize("opencode 2.0.30 starting\n[INFO] listening on stdio");

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Contains("all good", result.FinalMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("listening on stdio", result.FinalMessage, StringComparison.Ordinal);
        Assert.Contains("[INFO] listening on stdio", await ReadLogAsync(job.Job.StdoutLog), StringComparison.Ordinal);
        Assert.Equal(["init-1", "new-2", "config-3", "config-4", "prompt-5"], ClientFrames(job).Select(IdOf));
    }

    [Fact]
    public async Task WhenNobodyAnswersWithinTheReplyWindowTheNoAnswerNoticeIsTheNextPromptAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("anyone there?"), FakeAcpTurn.Report("decided alone"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(replyTimeoutSeconds: 1));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(LiveSession.NoAnswerNotice(TimeSpan.FromSeconds(1)), job.Acp.PromptText(2));
        Assert.Contains(job.Conversation(), entry => entry.Kind == ConversationKind.Notice && entry.Text == LiveSession.NoAnswerNotice(TimeSpan.FromSeconds(1)));
    }

    // ---- what the agent asks the client ---------------------------------------------------------------------------------------------------

    private static FakeAcpTurn Asking(params FakeAcpRequest[] requests) => new("done", Summary: "ok", Requests: requests, Tools: [new FakeAcpTool("shell", "the-command")]);

    [Fact]
    public async Task APermissionRequestIsAnsweredAllowOnceAndTheTurnGoesOnAsync()
    {
        using LiveJobHarness job = Job(Asking(FakeAcpRequest.Permission("ls -la")));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal([/*lang=json,strict*/ """{"jsonrpc":"2.0","id":7000,"result":{"outcome":{"outcome":"selected","optionId":"allow_once"}}}"""], job.Acp.Answers);
    }

    [Fact]
    public async Task ADeniedCommandIsRejectedAndOneThatIsNotIsAllowedInTheSameTurnAsync()
    {
        using LiveJobHarness job = Job(Asking(FakeAcpRequest.Permission("git push origin HEAD"), FakeAcpRequest.Permission("ls && git status")));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, deny: ["git push"]));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(
        [
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":7000,"result":{"outcome":{"outcome":"selected","optionId":"reject_once"}}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":7001,"result":{"outcome":{"outcome":"selected","optionId":"allow_once"}}}""",
        ], job.Acp.Answers);
    }

    [Fact]
    public async Task ADeniedCommandWithNoRejectOptionIsAnsweredCancelledNeverAllowedAsync()
    {
        using LiveJobHarness job = Job(Asking(FakeAcpRequest.Permission("ls ; git push", "allow_once", "allow_always")));

        await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, deny: ["git push"]));

        Assert.Equal([/*lang=json,strict*/ """{"jsonrpc":"2.0","id":7000,"result":{"outcome":{"outcome":"cancelled"}}}"""], job.Acp.Answers);
    }

    [Fact]
    public async Task ARequestForAClientCapabilityThatWasNeverAdvertisedIsMethodNotFoundAsync()
    {
        using LiveJobHarness job = Job(Asking(new FakeAcpRequest("fs/read_text_file", /*lang=json,strict*/ """{"sessionId":"@SESSION@","path":"/etc/hosts"}"""), new FakeAcpRequest("terminal/create", /*lang=json,strict*/ """{"sessionId":"@SESSION@","command":"ls"}""")));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(
        [
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":7000,"error":{"code":-32601,"message":"client does not implement fs/read_text_file"}}""",
            /*lang=json,strict*/ """{"jsonrpc":"2.0","id":7001,"error":{"code":-32601,"message":"client does not implement terminal/create"}}""",
        ], job.Acp.Answers);
    }

    // The answers are frames the client sent: they sit in its log after the prompt that made the agent ask.
    [Fact]
    public async Task TheClientsAnswersToTheAgentAreInTheClientLogAfterThePromptAsync()
    {
        using LiveJobHarness job = Job(Asking(FakeAcpRequest.Permission("ls")));

        await LiveJobHarness.FinishedAsync(job.StartAsync(live: false));

        Assert.Equal(["initialize", "session/new", "session/set_config_option", "session/set_config_option", "session/prompt", "(response)"], ClientMethods(job));
        Assert.Equal([.. job.Acp.Received, .. job.Acp.Answers], ClientFrames(job));
    }

    // The brief goes over stdin as a JSON-RPC request, not on argv: a big one and a non-ASCII one arrive whole.
    [Fact]
    public async Task ALargeNonAsciiBriefReachesTheAgentIntactAsync()
    {
        string line = "h\u00e9llo \ud83d\ude00 \u65e5\u672c\u8a9e \"quoted\" back\\slash `fence` {\"a\":1}";
        string brief = string.Join('\n', Enumerable.Repeat(line, 6000));
        Assert.True(brief.Length > 250_000);
        using LiveJobHarness job = Job(FakeAcpTurn.Report("read it all"));

        RunResult result = await LiveJobHarness.FinishedAsync(job.StartAsync(live: false, brief: brief));

        Assert.Equal(RunStatus.Success, result.Status);
        string received = job.Acp.PromptText(1);
        Assert.StartsWith(brief, received, StringComparison.Ordinal);
        Assert.True(received.Length >= brief.Length);
    }

    // `claustrum-note:` lines are not recorded as they are printed (the reply streams in chunks): they land in the conversation when the turn ends, with the text.
    [Fact]
    public async Task ANoteInALiveTurnLandsInTheConversationWhenTheTurnEndsAsync()
    {
        using LiveJobHarness job = Job(new FakeAcpTurn("Looking.\nclaustrum-note: reading the schema\nwhich database?"), FakeAcpTurn.Report("used it", "ready"));
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();
        job.Say("sqlite");

        await LiveJobHarness.FinishedAsync(run);

        Assert.Contains(job.Conversation(), entry => entry.Text.Contains("claustrum-note: reading the schema", StringComparison.Ordinal));
        Assert.Contains(job.Conversation(), entry => entry.Kind == ConversationKind.Question && entry.Text.Contains("which database?", StringComparison.Ordinal));
    }
}
