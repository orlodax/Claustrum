using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Jobs;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 2 `run --detach` and `jobs cancel` through the built binary against a conversing fake claude: the job is minted, a
// `claustrum jobs host` (a process of its own) runs it, and `run` returns. DetachedRepo kills every host it left, pass or fail.
public sealed class DetachedRunEndToEndTests : IDisposable
{
    private static readonly string[] hostFiles = ["host.json", "host-request.json"];

    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private static FakeClaudeScript Waiting() => new() { Turns = [FakeClaudeTurn.Ask("hold on")] };

    [Fact]
    public async Task DetachPrintsTheIdAndReturnsWhileTheHostKeepsTheJobRunningAsync()
    {
        fixture.Repo.Script(Waiting());

        CliResult process = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--reply-timeout", "600");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string id = process.Stdout.Trim();
        Assert.True(JobDirectory.IsJobId(id), $"stdout is the job id and nothing else: '{process.Stdout}'");
        Assert.Equal(id, fixture.OnlyJob());

        // `run` is gone and the job is not done: its receipt does not exist, and its host is a live process that is not run's child.
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "result.json")));
        Assert.True(fixture.HostAlive(id));
        int hostPid = fixture.Host(id)?.Pid ?? 0;

        // The host's first act is setsid(): a session and group of its own, so a hangup or Ctrl-C on the starter's terminal never reaches it.
        if (DetachedRepo.SessionOf(hostPid) is not null)
            await DetachedRepo.WaitUntilAsync(() => DetachedRepo.SessionOf(hostPid) == hostPid, $"host {hostPid} to lead a session of its own");

        await fixture.WaitForQuestionAsync(id);
        Assert.True(fixture.HostAlive(id), "the host outlives the process that started it");
        Assert.True(File.Exists(Path.Combine(fixture.JobDir(id), "host-request.json")));
        Assert.Empty(Directory.GetFiles(fixture.JobDir(id), "*.tmp"));
    }

    [Fact]
    public async Task DetachJsonPrintsTheStartedDocumentAndTheHostedJobFinishesWithAReceiptAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("hosted")] });

        CliResult process = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal(["status", "job_id"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("started", document.RootElement.GetProperty("status").GetString());
        string id = document.RootElement.GetProperty("job_id").GetString() ?? "";

        await fixture.WaitForResultAsync(id);
        JsonElement receipt = fixture.Json(id, "result.json") ?? throw new InvalidOperationException("no receipt");
        Assert.Equal("success", receipt.GetProperty("status").GetString());
        Assert.Equal(id, receipt.GetProperty("job_id").GetString());
        Assert.Equal("hosted", receipt.GetProperty("report").GetProperty("data").GetProperty("summary").GetString());
        Assert.Equal("ok", receipt.GetProperty("report_status").GetString());
        await fixture.WaitForHostGoneAsync(id);
        Assert.Empty(Directory.GetFiles(fixture.JobDir(id), "*.tmp"));
    }

    [Theory]
    [InlineData("--stream")]
    [InlineData("--stream-raw")]
    public async Task DetachWithStreamIsAUsageErrorAndMintsNothingAsync(string streamFlag)
    {
        CliResult process = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", streamFlag);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("--detach and --stream cannot be combined", process.Stderr, StringComparison.Ordinal);
        Assert.Empty(fixture.JobIds());
    }

    [Fact]
    public async Task CancelEndsAWaitingDetachedJobCancelledWithItsReceiptAsync()
    {
        fixture.Repo.Script(Waiting());
        CliResult started = await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--reply-timeout", "600");
        string id = started.Stdout.Trim();
        await fixture.WaitForQuestionAsync(id);

        CliResult cancel = await fixture.Repo.RunAsync("jobs", "cancel", id);

        Assert.True(cancel.ExitCode == ExitCodes.Ok, cancel.Stderr);
        Assert.StartsWith("━━━ cancelled", cancel.Stdout, StringComparison.Ordinal);
        Assert.Equal("cancelled", fixture.ResultStatus(id));
        await fixture.WaitForHostGoneAsync(id);
    }

    [Fact]
    public async Task CancelRefusesAnUnknownJobADoneJobAndAJobWithNoHostAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("quick")] });
        string done = (await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach")).Stdout.Trim();
        await fixture.WaitForResultAsync(done);
        string inProcess = $"20261015-190000-{Guid.NewGuid().ToString("N")[..8]}";
        Directory.CreateDirectory(fixture.JobDir(inProcess));
        File.WriteAllText(Path.Combine(fixture.JobDir(inProcess), "request.json"), /*lang=json,strict*/ """{"role":"builder"}""");

        CliResult unknown = await fixture.Repo.RunAsync("jobs", "cancel", "20261015-000000-deadbeef");
        CliResult finished = await fixture.Repo.RunAsync("jobs", "cancel", done);
        CliResult hostless = await fixture.Repo.RunAsync("jobs", "cancel", inProcess);

        Assert.Equal(ExitCodes.Usage, unknown.ExitCode);
        Assert.Equal(ExitCodes.Usage, finished.ExitCode);
        Assert.Contains("is done: nothing to cancel", finished.Stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, hostless.ExitCode);
        Assert.Contains("is not a detached job", hostless.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(inProcess), "cancel")));
    }

    [Fact]
    public async Task CancelOfAJobWhoseHostDiedExitsUsageAndWritesNoMarkerAsync()
    {
        fixture.Repo.Script(Waiting());
        string id = (await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach", "--reply-timeout", "600")).Stdout.Trim();
        await fixture.WaitForQuestionAsync(id);
        fixture.KillHost(id);
        await fixture.WaitForHostGoneAsync(id);

        CliResult cancel = await fixture.Repo.RunAsync("jobs", "cancel", id);

        Assert.Equal(ExitCodes.Usage, cancel.ExitCode);
        Assert.Contains("abandoned", cancel.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.JobDir(id), "cancel")));
    }

    // The files a reader polls (host.json, host-request.json) are never seen cut short: a reader hammering the job directory while
    // `run --detach` writes them parses every version it catches.
    [Fact]
    public async Task HostJsonAndHostRequestJsonAreNeverSeenTruncatedAsync()
    {
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("fine")] });
        using CancellationTokenSource stop = new();
        Task<string[]> reader = fixture.WatchForTornFilesAsync(stop.Token, hostFiles);

        string started = (await fixture.Repo.RunAsync("run", "builder", "--brief", "x", "--detach")).Stdout.Trim();
        await fixture.WaitForResultAsync(started);
        await stop.CancelAsync();
        string[] torn = await reader;

        Assert.Empty(torn);
    }
}
