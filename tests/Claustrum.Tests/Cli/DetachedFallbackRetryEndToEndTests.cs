using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Coordination;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #126 final review, finding 2 (repro through the built binary): a fallback retry minted inside a detached host had no host.json, so `jobs cancel`
// refused it, a tree's settle skipped it and a killed waiter left it running and paying. A fake opencode answers the real 402, the repo's fake claude is
// the cast's fallback and asks a question, so the retry is a live job in the same host as the first. DetachedRepo kills every host it left.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DetachedFallbackRetryEndToEndTests : IDisposable
{
    private static readonly TimeSpan within = TimeSpan.FromSeconds(25);

    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private void Arrange(params FakeClaudeTurn[] turns)
    {
        FallbackSetup.Apply(fixture.Repo, FakeRefusals.RealQuotaEvents());
        fixture.Repo.Script(new FakeClaudeScript { Turns = turns.Length > 0 ? turns : [FakeClaudeTurn.Ask("hold on"), FakeClaudeTurn.Report("done on the fallback")] });
    }

    private string? RetriedAs(string id) =>
        fixture.Json(id, "result.json") is { } receipt && receipt.TryGetProperty("retried_as", out JsonElement retried) && retried.ValueKind == JsonValueKind.String ? retried.GetString() : null;

    // `run --detach`: the first job is refused by its provider and the host runs the retry; returns once the retry is asking its question.
    private async Task<(string First, string Retry)> StartDetachedAsync(IReadOnlyDictionary<string, string?>? tree = null)
    {
        CliResult started = tree is null
            ? await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--reply-timeout", "600")
            : await fixture.Repo.RunWithEnvAsync(["run", "builder", "--brief", "x", "--detach", "--reply-timeout", "600"], tree);
        Assert.True(started.ExitCode == ExitCodes.Ok, started.Stderr);
        string first = started.Stdout.Trim();

        await DetachedRepo.WaitUntilAsync(() => RetriedAs(first) is not null, $"job {first} to name its retry", within);
        string retry = RetriedAs(first) ?? throw new InvalidOperationException(first);
        await fixture.WaitForQuestionAsync(retry);
        return (first, retry);
    }

    private async Task AssertCancelledAsync(string first, string retry)
    {
        await fixture.WaitForResultAsync(retry, within);
        Assert.Equal("cancelled", fixture.ResultStatus(retry));
        Assert.Equal("failed", fixture.ResultStatus(first));
        Assert.Equal(retry, RetriedAs(first));
        await fixture.WaitForHostGoneAsync(first);
    }

    [Fact]
    public async Task TheRetryHasTheFirstJobsHostJsonSoItsHostIsRecordedAsTheOneRunningItAsync()
    {
        Arrange();

        (string first, string retry) = await StartDetachedAsync();

        Assert.NotEqual(first, retry);
        Claustrum.Live.HostRecord firstHost = fixture.Host(first) ?? throw new InvalidOperationException("the first job has no host.json");
        Claustrum.Live.HostRecord retryHost = fixture.Host(retry) ?? throw new InvalidOperationException("the retry has no host.json");
        Assert.Equal(firstHost.Pid, retryHost.Pid);
        Assert.Equal(firstHost.Tree, retryHost.Tree);
        Assert.True(fixture.HostAlive(retry));
        Assert.Empty(Directory.GetFiles(fixture.JobDir(retry), "host.json.*.tmp"));
    }

    [Fact]
    public async Task JobsCancelOnTheRetryCancelsItWithAReceiptAndTheHostEndsAsync()
    {
        Arrange();
        (string first, string retry) = await StartDetachedAsync();

        CliResult cancel = await fixture.Repo.RunAsync("jobs", "cancel", retry);

        Assert.True(cancel.ExitCode == ExitCodes.Ok, cancel.Stderr);
        Assert.DoesNotContain("was retried", cancel.Stderr, StringComparison.Ordinal);
        await AssertCancelledAsync(first, retry);
    }

    [Fact]
    public async Task JobsCancelOnTheFirstIdCancelsTheRetryAndSaysSoOnStderrAsync()
    {
        Arrange();
        (string first, string retry) = await StartDetachedAsync();

        CliResult cancel = await fixture.Repo.RunAsync("jobs", "cancel", first);

        Assert.True(cancel.ExitCode == ExitCodes.Ok, cancel.Stderr);
        Assert.Contains($"job {first} was retried on its cast's fallback as {retry}; going on with {retry}", cancel.Stderr, StringComparison.Ordinal);
        Assert.Contains($"cancelling {retry}", cancel.Stderr, StringComparison.Ordinal);
        await AssertCancelledAsync(first, retry);
    }

    // The waiter of a tree-routed child (`run` under a coordinate tree) is what a killed architect's terminal takes with it: SIGTERM on it must stop
    // the retry, which a waiter that still held the first job's id could not (its host.json was the retry's missing one).
    [Fact]
    public async Task ATreeRoutedWaiterKilledBySigtermLeavesNoRetryRunningAsync()
    {
        // A long turn, so the waiter is still waiting (a tree's `run` returns on a question) when the signal comes.
        Arrange(new FakeClaudeTurn("slow", SleepSeconds: 60));
        Dictionary<string, string?> tree = DetachedRepo.Tree(fixture.MintRoot());
        using RunningCli waiter = fixture.StartBuilder(tree, "--json");
        string retry = "";
        await waiter.WaitUntilAsync(
            () => fixture.JobIds().FirstOrDefault(id => RetriedAs(id) is not null) is { } first && (retry = RetriedAs(first) ?? "") is { Length: > 0 } && fixture.LiveState(retry) == "running",
            "the retry to be running", within, DetachedRepo.Ct);
        string first = Assert.Single(fixture.JobIds(), id => RetriedAs(id) == retry);

        await waiter.SendSignalAsync("TERM", DetachedRepo.Ct);
        await waiter.WaitForExitAsync(within, DetachedRepo.Ct);

        await AssertCancelledAsync(first, retry);
        Assert.False(fixture.HostAlive(retry), "no host, and so no fake claude under it, is left running for the retry");
    }

    // The waiter's host is killed (a crash): the retry has no receipt and no owner, so the waiter that followed retried_as reports it abandoned (exit 6).
    [Fact]
    public async Task AWaiterOfARetryWhoseHostDiedReportsItAbandonedNamingTheRetryAsync()
    {
        Arrange(new FakeClaudeTurn("slow", SleepSeconds: 60));
        Dictionary<string, string?> tree = DetachedRepo.Tree(fixture.MintRoot());
        using RunningCli waiter = fixture.StartBuilder(tree, "--json");
        string retry = "";
        await waiter.WaitUntilAsync(
            () => fixture.JobIds().FirstOrDefault(id => RetriedAs(id) is not null) is { } first && (retry = RetriedAs(first) ?? "") is { Length: > 0 } && fixture.LiveState(retry) == "running",
            "the retry to be running", within, DetachedRepo.Ct);
        string first = Assert.Single(fixture.JobIds(), id => RetriedAs(id) == retry);

        fixture.KillHost(first);
        int exit = await waiter.WaitForExitAsync(within, DetachedRepo.Ct);

        Assert.Equal(ExitCodes.Abandoned, exit);
        Assert.Contains(retry, waiter.Stdout, StringComparison.Ordinal);
        Assert.Contains("abandoned", waiter.Stdout + waiter.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(retry), "result.json")));
    }

    // ---- following retried_as -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SayOnTheFirstIdReachesTheRetryWithTheStderrNoteAndTheRetryFinishesAsync()
    {
        Arrange();
        (string first, string retry) = await StartDetachedAsync();

        CliResult said = await fixture.Repo.RunAsync("jobs", "say", first, "sqlite");

        Assert.True(said.ExitCode == ExitCodes.Ok, said.Stderr);
        Assert.Contains($"job {first} was retried on its cast's fallback as {retry}; going on with {retry}", said.Stderr, StringComparison.Ordinal);
        Assert.StartsWith("queued: answer to question", said.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("was retried", said.Stdout, StringComparison.Ordinal);
        await fixture.WaitForResultAsync(retry, within);
        Assert.Equal("success", fixture.ResultStatus(retry));
        Assert.Contains("sqlite", fixture.Conversation(retry), StringComparison.Ordinal);
        Assert.DoesNotContain("sqlite", fixture.Conversation(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyOnTheFirstIdAnswersTheRetryAndPrintsItsReceiptAsync()
    {
        Arrange();
        (string first, string retry) = await StartDetachedAsync();

        CliResult reply = await fixture.Repo.RunAsync("jobs", "reply", first, "sqlite", "--json");

        Assert.True(reply.ExitCode == ExitCodes.Ok, reply.Stderr + reply.Stdout);
        Assert.Contains($"job {first} was retried on its cast's fallback as {retry}", reply.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(reply.Stdout);
        Assert.Equal(retry, document.RootElement.GetProperty("job_id").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WaitOnTheFirstIdWaitsThroughToTheRetryAndSaysSoAsync()
    {
        Arrange(FakeClaudeTurn.Report("quick"));
        CliResult started = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach");
        string first = started.Stdout.Trim();

        CliResult wait = await fixture.Repo.RunAsync("jobs", "wait", first, "--timeout", "40", "--json");

        Assert.True(wait.ExitCode == ExitCodes.Ok, wait.Stderr + wait.Stdout);
        string retry = RetriedAs(first) ?? throw new InvalidOperationException("no retry");
        Assert.Contains($"job {first} was retried on its cast's fallback as {retry}", wait.Stderr, StringComparison.Ordinal);
        // The first job failed, so a success is the retry's.
        Assert.Contains("\"result_status\":\"success\"", wait.Stdout, StringComparison.Ordinal);
        Assert.Equal("failed", fixture.ResultStatus(first));
    }

    [Fact]
    public async Task FollowOnTheFirstIdGoesOnToTheRetrysLogAsync()
    {
        Arrange(FakeClaudeTurn.Report("quick"));
        CliResult started = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach");
        string first = started.Stdout.Trim();

        CliResult follow = await fixture.Repo.RunAsync("jobs", "follow", first);

        Assert.True(follow.ExitCode == ExitCodes.Ok, follow.Stderr);
        string retry = RetriedAs(first) ?? throw new InvalidOperationException("no retry");
        Assert.Contains($"job {first} was retried on its cast's fallback as {retry}; going on with {retry}", follow.Stderr, StringComparison.Ordinal);
        Assert.Contains(retry, follow.Stderr, StringComparison.Ordinal);
    }

    // ---- the tree's settle ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TreeSettlerCancelsAWaitingRetryOfADetachedChildOfTheTreeAsync()
    {
        Arrange();
        string root = fixture.MintRoot();
        (string first, string retry) = await StartDetachedAsync(DetachedRepo.Tree(root));
        using AppServicesScope scope = new(new CliHomePlatform(fixture.Repo.Home));

        string[] asked = TreeSettler.CancelRemaining(root);

        Assert.Equal([retry], asked);
        await AssertCancelledAsync(first, retry);
    }
}
