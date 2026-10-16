using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;
using static Claustrum.Tests.Testing.AcpEngine;

namespace Claustrum.Tests.Live;

// #130: a per-turn session cannot be told mid-turn, so the 85 % warning rides the next prompt (LiveSession.TakeQueued). Alone in its class because it needs twelve seconds of the agent's time.
public sealed class AcpLiveEngineWarningTests
{
    private static LiveJobHarness Job(params FakeAcpTurn[] turns) => LiveJobHarness.ForAcp(turns);

    // A per-turn session cannot be told mid-turn: the 85 % warning rides the next prompt, after whatever the human said (LiveSession.TakeQueued).
    [Fact]
    public async Task TheEightyFivePercentWarningRidesTheNextPromptAfterTheMessageThatWaitedForItAsync()
    {
        using LiveJobHarness job = Job(FakeAcpTurn.Ask("anything else?", sleepSeconds: 12), FakeAcpTurn.Report("ok"));
        Task<RunResult> run = job.StartAsync(timeout: TimeSpan.FromSeconds(14));
        await WaitForPromptsAsync(job, 1);

        job.Say("sqlite");
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Success, result.Status);
        string second = job.Acp.PromptText(2);
        Assert.StartsWith("sqlite\n\n", second, StringComparison.Ordinal);
        Assert.Contains("before this job is stopped", second, StringComparison.Ordinal);
        Assert.Contains("write your claustrum-report now", second, StringComparison.Ordinal);
        Assert.Single(job.Conversation(), entry => entry.Kind == ConversationKind.Notice);
    }
}
