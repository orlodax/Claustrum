using Claustrum.Cli;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// #124: what was typed before a question existed must not answer it, so the terminal's type-ahead is discarded before every
// `reply> ` and never after one. The seam is IPromptTerminal: a stand-in records the calls QuestionPrompter makes in order, and the
// harness runs a real session against a conversing fake claude underneath.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class QuestionPrompterTerminalTests(AppServicesHomeFixture fixture)
{
    private sealed class RecordingTerminal(params string?[] lines) : IPromptTerminal
    {
        private int next;

        public List<string> Calls { get; } = [];

        public void DiscardPending() => Calls.Add("discard");

        public Task<string?> ReadLineAsync()
        {
            Calls.Add("read");
            return Task.FromResult(lines[next++]);
        }
    }

    [Fact]
    public async Task TheTypeAheadIsDiscardedBeforeEveryPromptAndNeverAfterOneAsync()
    {
        using LiveJobHarness job = new(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("one?"), FakeClaudeTurn.Ask("two?"), FakeClaudeTurn.Report("both")] }, fixture.Platform);
        RecordingTerminal terminal = new("first", "second");
        Task<RunResult> run = job.StartAsync();

        RunResult result = await LiveJobHarness.FinishedAsync(Task.Run(() => LiveRun.AnswerWhileAsync(run, job.Job, () => terminal), TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(["discard", "read", "discard", "read"], terminal.Calls);
        Assert.Contains(job.Input(), line => line.Contains("first", StringComparison.Ordinal));
        Assert.Contains(job.Input(), line => line.Contains("second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyLineIsNotAnAnswerAndTheSameQuestionIsNotAskedAgainAsync()
    {
        using LiveJobHarness job = new(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("one?"), FakeClaudeTurn.Report("done")] }, fixture.Platform);
        RecordingTerminal terminal = new("");
        Task<RunResult> run = job.StartAsync();
        Task<RunResult> answering = Task.Run(() => LiveRun.AnswerWhileAsync(run, job.Job, () => terminal), TestContext.Current.CancellationToken);

        await LiveJobHarness.WaitUntilAsync(() => terminal.Calls.Count == 2, "the one prompt");
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(["discard", "read"], terminal.Calls);
        job.Say("later, from another shell");
        RunResult result = await LiveJobHarness.FinishedAsync(answering);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(["discard", "read"], terminal.Calls);
    }
}
