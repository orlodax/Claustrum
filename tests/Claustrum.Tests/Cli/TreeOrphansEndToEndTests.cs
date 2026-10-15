using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 2 review F1: no paid child outlives the coordinate job it hangs from. A detached host watches its tree's root
// (a result.json written, or its owner process gone) and cancels its job; `coordinate` cancels its tree's remaining detached children
// when its architect's run ends. The roots here are job directories with a `sleep` as owner (DetachedRepo.MintRoot), and one
// real `coordinate` run whose fake architect starts detached children.
public sealed class TreeOrphansEndToEndTests : IDisposable
{
    private const string RootEnded = "The coordinate job that started this one has ended; this job was cancelled.";

    private static readonly TimeSpan cancelWithin = TimeSpan.FromSeconds(20);

    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private static FakeClaudeScript Waiting() => new() { Turns = [FakeClaudeTurn.Ask("hold on")] };

    private async Task<string> StartChildAsync(string root)
    {
        CliResult started = await fixture.Repo.RunWithEnvAsync(["run", "builder", "--brief", "x", "--detach"], DetachedRepo.Tree(root));
        Assert.True(started.ExitCode == ExitCodes.Ok, started.Stderr);
        return started.Stdout.Trim();
    }

    private async Task AssertCancelledForTheEndedRootAsync(string child)
    {
        await fixture.WaitForResultAsync(child, cancelWithin);
        Assert.Equal("cancelled", fixture.ResultStatus(child));
        Assert.Contains(RootEnded, fixture.Conversation(child), StringComparison.Ordinal);
        Assert.Contains(RootEnded, File.ReadAllText(Path.Combine(fixture.JobDir(child), "host.log")), StringComparison.Ordinal);
        await fixture.WaitForHostGoneAsync(child);
    }

    [Fact]
    public async Task AHostedChildWhoseRootOwnerDiesCancelsItselfWithTheNoticeAndAReceiptAsync()
    {
        fixture.Repo.Script(Waiting());
        string root = fixture.MintRoot();
        string child = await StartChildAsync(root);
        await fixture.WaitForQuestionAsync(child);
        Assert.True(fixture.HostAlive(child));

        fixture.KillRootOwner(root);

        await AssertCancelledForTheEndedRootAsync(child);
    }

    [Fact]
    public async Task AHostedChildWhoseRootWritesItsReceiptCancelsItselfTheSameWayAsync()
    {
        fixture.Repo.Script(Waiting());
        string root = fixture.MintRoot();
        string child = await StartChildAsync(root);
        await fixture.WaitForQuestionAsync(child);

        fixture.WriteRootReceipt(root);

        await AssertCancelledForTheEndedRootAsync(child);
    }

    [Fact]
    public async Task AHostStartedUnderARootThatIsAlreadyGoneEndsAtOnceAsync()
    {
        fixture.Repo.Script(Waiting());
        string root = fixture.MintRoot();
        fixture.KillRootOwner(root);

        string child = await StartChildAsync(root);

        await fixture.WaitForResultAsync(child, cancelWithin);
        Assert.Equal("cancelled", fixture.ResultStatus(child));
        await fixture.WaitForHostGoneAsync(child);
    }

    // A tree id that names no job, or a job that never recorded an owner (an id exported by hand), is not a root: nothing is cancelled for it.
    // The watch checks at the start and every 3 s; waiting past two checks is what shows it never fires.
    [Fact]
    public async Task ATreeIdThatIsNoRootCancelsNothingAsync()
    {
        fixture.Repo.Script(Waiting());
        string noJob = "20261015-120000-feedface";
        string noOwner = fixture.MintRoot(withOwner: false);
        string first = await StartChildAsync(noJob);
        string second = await StartChildAsync(noOwner);
        await fixture.WaitForQuestionAsync(first);
        await fixture.WaitForQuestionAsync(second);

        await Task.Delay(TimeSpan.FromSeconds(7), DetachedRepo.Ct);

        foreach (string child in new[] { first, second })
        {
            Assert.True(fixture.HostAlive(child), $"{child} was cancelled for a root that is none");
            Assert.Equal("waiting_for_input", fixture.LiveState(child));
            Assert.False(File.Exists(Path.Combine(fixture.JobDir(child), "result.json")));
        }
    }
}
