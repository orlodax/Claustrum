using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 3a through the built binary against a fake `opencode` (FakeOpencode: one process per turn): `--stream` and `jobs follow` see every turn of a live
// session, `jobs say` answers it from another process, and `jobs answer` on an opencode job that stopped with needs_input bills only what the continuation
// added (the session export after minus before). The configured path is what runs and the child's PATH holds no opencode, so nothing paid can start.
public sealed class OpencodeLiveEndToEndTests : IDisposable
{
    private const string Asks =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":[],"allow_free_form":true}]}""";

    private static readonly TimeSpan limit = TimeSpan.FromSeconds(30);

    private readonly IsolatedRepo repo = new();
    private readonly List<RunningCli> started = [];
    private readonly string fakeDirectory;

    public OpencodeLiveEndToEndTests()
    {
        FakeOpencode.RequirePosixShellForHarness();
        repo.WriteCast(null);
        fakeDirectory = Directory.CreateDirectory(Path.Combine(repo.Home, "fake-opencode")).FullName;
    }

    public void Dispose()
    {
        foreach (RunningCli cli in started)
        {
            cli.Kill();
            cli.Dispose();
        }

        repo.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private FakeOpencode Fake(decimal startingCostUsd = 0m, params FakeOpencodeTurn[] turns)
    {
        FakeOpencode fake = new(fakeDirectory, turns, startingCostUsd);
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{" + fake.BackendJson + "}}");
        return fake;
    }

    private RunningCli Start(params string[] args)
    {
        RunningCli cli = repo.StartCli(args);
        started.Add(cli);
        return cli;
    }

    private static readonly string[] opencodeRun = ["run", "builder", "--brief", "do the task", "--backend", "opencode", "--model", "opencode/fake"];

    private string TheJob() => Assert.Single(repo.JobDirectories());

    private string ConversationOf(string id) => ReadShared(Path.Combine(repo.JobsRoot, id, "conversation.jsonl"));

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private async Task WaitForQuestionAsync(RunningCli run) =>
        await run.WaitUntilAsync(
            () => repo.JobDirectories() is [{ } id] && ConversationOf(id).Contains("\"kind\":\"question\"", StringComparison.Ordinal) && ReadShared(Path.Combine(repo.JobsRoot, id, "live.json")).Contains("waiting_for_input", StringComparison.Ordinal),
            "the opencode job to wait for input",
            limit,
            Ct);

    [Fact]
    public async Task StreamShowsEveryTurnOfALiveSessionAndSayFromAnotherProcessAnswersItAsync()
    {
        FakeOpencode fake = Fake(0m, FakeOpencodeTurn.Ask("which database?"), FakeOpencodeTurn.Report("used it", "ready to go"));
        RunningCli run = Start([.. opencodeRun, "--reply-timeout", "60", "--stream"]);
        await WaitForQuestionAsync(run);
        string id = TheJob();

        CliResult said = await repo.RunAsync("jobs", "say", id, "sqlite");
        int exit = await run.WaitForExitAsync(limit, Ct);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.True(exit == ExitCodes.Ok, run.Stderr + run.Stdout);
        string everything = run.Stderr + run.Stdout;
        Assert.Contains("which database?", everything, StringComparison.Ordinal);
        Assert.Contains("ready to go", everything, StringComparison.Ordinal);
        Assert.Equal(2, fake.Runs);
        Assert.Equal("sqlite", fake.Message(2));
        Assert.Equal(["--session", FakeOpencode.Session, "--", "sqlite"], fake.Argv(2)[^4..]);
    }

    [Fact]
    public async Task FollowSeesEveryTurnAndEndsWithTheJobAsync()
    {
        Fake(0m, FakeOpencodeTurn.Ask("which database?"), FakeOpencodeTurn.Report("used it", "ready to go"));
        RunningCli run = Start([.. opencodeRun, "--reply-timeout", "60"]);
        await WaitForQuestionAsync(run);
        string id = TheJob();
        RunningCli follow = Start("jobs", "follow", id);
        await follow.WaitUntilAsync(() => (follow.Stderr + follow.Stdout).Contains("which database?", StringComparison.Ordinal), "follow to show turn 1", limit, Ct);

        CliResult said = await repo.RunAsync("jobs", "say", id, "sqlite");
        int runExit = await run.WaitForExitAsync(limit, Ct);
        int followExit = await follow.WaitForExitAsync(limit, Ct);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.Equal(ExitCodes.Ok, runExit);
        Assert.Equal(ExitCodes.Ok, followExit);
        string shown = follow.Stderr + follow.Stdout;
        Assert.Contains("ready to go", shown, StringComparison.Ordinal);
        Assert.Contains("> sqlite", shown.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        // One continuous stdout.log holds both turns' events.
        string log = ReadShared(Path.Combine(repo.JobsRoot, id, "stdout.log"));
        Assert.Contains("which database?", log, StringComparison.Ordinal);
        Assert.Contains("ready to go", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpencodeJobThatNeedsInputIsAnsweredBilledOnlyForWhatTheContinuationAddedAsync()
    {
        Fake(0m, FakeOpencodeTurn.NeedsInput(Asks, costUsd: 0.10m));
        (CliResult first, JsonElement asked) = await repo.RunBuilderAsync([.. opencodeRun[4..], "--no-live"]);
        string id = Str(asked, "job_id");
        Assert.Equal(ExitCodes.NeedsInput, first.ExitCode);
        Assert.Equal(0.10m, asked.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(FakeOpencode.Session, Str(asked, "session_id"));

        // The session already costs 0.10; the answer's process adds 0.05.
        FakeOpencode again = Fake(0.10m, FakeOpencodeTurn.Report("finished", costUsd: 0.05m));
        CliResult answer = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        Assert.True(answer.ExitCode == ExitCodes.Ok, answer.Stderr + answer.Stdout);
        using JsonDocument document = JsonDocument.Parse(answer.Stdout);
        Assert.Equal("success", Str(document.RootElement, "status"));
        Assert.Equal(0.05m, document.RootElement.GetProperty("cost_usd").GetDecimal());
        string[] argv = again.Argv(again.Runs);
        Assert.Equal(FakeOpencode.Session, argv[Array.IndexOf(argv, "--session") + 1]);
        Assert.Equal("opencode/fake", argv[Array.IndexOf(argv, "--model") + 1]);
    }
}
