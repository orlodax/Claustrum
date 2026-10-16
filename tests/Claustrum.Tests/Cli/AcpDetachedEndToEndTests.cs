using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #130 under `run --detach`: the job is run by a `claustrum jobs host` of its own, which rebuilds the request from host-request.json, so the ACP backend, its model and its
// configured path must survive that trip, and the agent the host started must go with the job (answered, or cancelled). DetachedRepo kills every host it left.
public sealed class AcpDetachedEndToEndTests : IDisposable
{
    private static readonly string[] detachedRun = ["run", "builder", "--brief", "x", "--backend", "acp-opencode", "--model", "opencode/fake", "--detach", "--reply-timeout", "600"];

    private readonly DetachedRepo fixture = new();

    public AcpDetachedEndToEndTests() => FakeAcpAgent.RequirePosixShell();

    public void Dispose() => fixture.Dispose();

    private FakeAcpAgent Fake(params FakeAcpTurn[] turns)
    {
        string directory = Directory.CreateDirectory(Path.Combine(fixture.Repo.Home, "fake-acp")).FullName;
        FakeAcpAgent fake = new(directory, turns);
        File.WriteAllText(Path.Combine(fixture.Repo.Repo, "claustrum.json"), "{\"backends\":{" + fake.BackendJson + "}}");
        return fake;
    }

    [Fact]
    public async Task ADetachedAcpJobWaitsForItsQuestionIsAnsweredFromAnotherProcessAndFinishesWithAReceiptAsync()
    {
        FakeAcpAgent fake = Fake(FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it", "ready"));

        CliResult started = await fixture.Repo.RunAsync(detachedRun);
        Assert.True(started.ExitCode == ExitCodes.Ok, started.Stderr);
        string id = started.Stdout.Trim();
        await fixture.WaitForQuestionAsync(id);
        Assert.Equal(1, fake.Runs);
        Assert.True(FakeAcpAgent.IsRunning(fake.Pid(1)), "the hosted job's agent is alive while it waits");

        CliResult said = await fixture.Repo.RunAsync("jobs", "say", id, "sqlite");
        await fixture.WaitForResultAsync(id);
        await fixture.WaitForHostGoneAsync(id);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        JsonElement receipt = fixture.Json(id, "result.json") ?? throw new InvalidOperationException("no receipt");
        Assert.Equal("success", receipt.GetProperty("status").GetString());
        Assert.Equal("acp-opencode", receipt.GetProperty("backend").GetString());
        Assert.Equal("opencode/fake", receipt.GetProperty("model").GetString());
        Assert.Equal(0.02m, receipt.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(2, fake.Prompts);
        Assert.Equal("sqlite", fake.PromptText(2));
        Assert.Equal(1, fake.Runs);
        await DetachedRepo.WaitUntilAsync(() => !FakeAcpAgent.IsRunning(fake.Pid(1)), "the agent to be gone with its job");
    }

    [Fact]
    public async Task CancellingAWaitingDetachedAcpJobKillsItsAgentAsync()
    {
        FakeAcpAgent fake = Fake(FakeAcpTurn.Ask("hold on"));
        string id = (await fixture.Repo.RunAsync(detachedRun)).Stdout.Trim();
        await fixture.WaitForQuestionAsync(id);
        int agent = fake.Pid(1);
        Assert.True(FakeAcpAgent.IsRunning(agent));

        CliResult cancel = await fixture.Repo.RunAsync("jobs", "cancel", id);

        Assert.True(cancel.ExitCode == ExitCodes.Ok, cancel.Stderr);
        Assert.Equal("cancelled", fixture.ResultStatus(id));
        await fixture.WaitForHostGoneAsync(id);
        await DetachedRepo.WaitUntilAsync(() => !FakeAcpAgent.IsRunning(agent), "the agent to be killed with its job");
    }
}
