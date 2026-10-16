using Claustrum.Cli;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// #126: `run`'s terminal prompt (LiveRun.AnswerWhileAsync) watches the job it minted; when that job is retried on its cast's fallback, the conversation
// to show and the question to ask are the retry's. A live fake opencode is refused with the real 402, the fallback is a conversing fake claude that asks.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class FallbackTerminalPromptTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

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
    public async Task AQuestionTheRetryAsksIsPutToTheTerminalAndItsAnswerReachesTheRetryAsync()
    {
        FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Ask("which database?"), FakeClaudeTurn.Report("done on the fallback", echoReply: true)], LiveInputLog = Path.Combine(repo.Home, "input.log") });
        DelegateRequest request = new(
            Role: "builder", Brief: "do the task", Cwd: repo.Repo, Tier: "high", Overrides: new ConfigOverrides(Model: FallbackSetup.Primary),
            ResumeSession: null, AttachFiles: [], Env: [], Stream: false, DiffCapBytes: 64 * 1024,
            Live: true, ReplyTimeoutSeconds: 60, Fallback: FallbackSetup.Fallback);
        PreparedDelegation prepared = DelegateEngine.Prepare(request);
        JobPaths job = JobDirectory.Create(fixture.Platform);
        RecordingTerminal terminal = new("sqlite");
        Task<RunResult> run = Task.Run(() => DelegateEngine.RunAsync(prepared, job, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        RunResult result = await LiveJobHarness.FinishedAsync(Task.Run(() => LiveRun.AnswerWhileAsync(run, job, () => terminal), TestContext.Current.CancellationToken));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal("claude", result.Backend);
        Assert.Equal(job.Id, result.FallbackOf);
        Assert.NotEqual(job.Id, result.JobId);
        Assert.Equal(["discard", "read"], terminal.Calls);
        Assert.Contains("sqlite", File.ReadAllText(Path.Combine(repo.Home, "input.log")), StringComparison.Ordinal);
    }
}
