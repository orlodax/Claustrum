using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Live;

// Review F2 on #124: whatever sits in inbox/ is read without ever blocking the pump. A FIFO, a link, an oversized file and one
// that is no message are each discarded with a Notice that says why; a cancel still completes with a receipt.
public sealed class LiveInboxSafetyTests
{
    private static FakeClaudeScript Asking() => new() { Turns = [FakeClaudeTurn.Ask("ready?"), FakeClaudeTurn.Report("fine")] };

    private static ConversationEntry[] Discards(LiveJobHarness job) =>
        [.. job.Conversation().Where(entry => entry.Text.StartsWith("Discarded an inbox message", StringComparison.Ordinal))];

    [Fact]
    public async Task AFifoALinkAnOversizedFileAndAnUnparsableOneAreEachDiscardedWithANoticeAsync()
    {
        using LiveJobHarness job = new(Asking());
        Task<RunResult> run = job.StartAsync();
        await job.WaitForQuestionAsync();

        string target = Path.Combine(job.Cwd, "target.txt");
        File.WriteAllText(target, "not a message");
        PipeFiles.MakeFifo(Path.Combine(job.InboxDirectory, "1-fifo.json"));
        TestLinks.Create(Path.Combine(job.InboxDirectory, "2-link.json"), target);
        File.WriteAllBytes(Path.Combine(job.InboxDirectory, "3-big.json"), new byte[LiveSession.MaxInboxBytes + 10]);
        File.WriteAllText(Path.Combine(job.InboxDirectory, "4-bad.json"), "{ this is not json");

        await LiveJobHarness.WaitUntilAsync(() => Discards(job).Length == 4, "four discard notices");
        string[] notices = [.. Discards(job).Select(entry => entry.Text)];
        Assert.Contains(notices, text => text.Contains("1-fifo.json", StringComparison.Ordinal));
        Assert.Contains(notices, text => text.Contains("2-link.json", StringComparison.Ordinal) && text.Contains("it is a link", StringComparison.Ordinal));
        Assert.Contains(notices, text => text.Contains("3-big.json", StringComparison.Ordinal) && text.Contains("it is over 1024 KiB", StringComparison.Ordinal));
        Assert.Contains(notices, text => text.Contains("4-bad.json", StringComparison.Ordinal) && text.Contains("it is not a message", StringComparison.Ordinal));
        Assert.All(Discards(job), entry => Assert.Equal(ConversationParty.Claustrum, entry.From));

        // None of them answered the question, and the session still takes a real reply.
        Assert.Equal(LiveStatus.WaitingForInput, job.State()?.State);
        job.Say("yes");
        RunResult result = await LiveJobHarness.FinishedAsync(run);
        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Empty(Directory.GetFileSystemEntries(job.InboxDirectory));
        Assert.Equal(2, job.Input().Length);
    }

    [Fact]
    public async Task ACancelWithAFifoInTheInboxStillCompletesWithAReceiptAsync()
    {
        using LiveJobHarness job = new(Asking());
        using CancellationTokenSource cancel = new();
        Task<RunResult> run = job.StartAsync(cancel: cancel.Token);
        await job.WaitForQuestionAsync();
        PipeFiles.MakeFifo(Path.Combine(job.InboxDirectory, "1-fifo.json"));

        await cancel.CancelAsync();
        RunResult result = await LiveJobHarness.FinishedAsync(run);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.True(File.Exists(job.Job.ResultJson));
        Assert.NotNull(result.Conversation);
        Assert.Equal(LiveStatus.Done, job.State()?.State);
    }
}
