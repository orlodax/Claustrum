using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #130 through the built binary against a fake `opencode acp` (FakeAcpAgent: one process for the whole session): `--stream` and `jobs follow` see every turn of a
// live session, `jobs say` answers it from another process, `jobs answer` on a job that stopped with needs_input resumes it through `session/load` and bills only what
// the continuation added, and a refusal in the handshake, a timeout and a bad `--backend` end the way their receipts say. The configured path is what runs and the
// child's PATH holds no opencode, so nothing paid can start.
public sealed class AcpLiveEndToEndTests : IDisposable
{
    private const string Asks =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":[],"allow_free_form":true}]}""";

    private static readonly TimeSpan limit = TimeSpan.FromSeconds(30);

    private readonly IsolatedRepo repo = new();
    private readonly List<RunningCli> started = [];
    private int fakes;

    public AcpLiveEndToEndTests()
    {
        FakeAcpAgent.RequirePosixShell();
        repo.WriteCast(null);
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

    // Each fake gets a directory of its own, so its recordings are one run's: a second agent (the continuation) is not mixed with the first.
    private FakeAcpAgent Fake(decimal startingCostUsd = 0m, params FakeAcpTurn[] turns)
    {
        string directory = Directory.CreateDirectory(Path.Combine(repo.Home, $"fake-acp-{++fakes}")).FullName;
        FakeAcpAgent fake = new(directory, turns, startingCostUsd);
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{" + fake.BackendJson + "}}");
        return fake;
    }

    private RunningCli Start(params string[] args)
    {
        RunningCli cli = repo.StartCli(args);
        started.Add(cli);
        return cli;
    }

    private static readonly string[] acpRun = ["run", "builder", "--brief", "do the task", "--backend", "acp-opencode", "--model", "opencode/fake"];

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
            "the acp job to wait for input",
            limit,
            Ct);

    // ---- a live session ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StreamShowsEveryTurnOfALiveSessionAndSayFromAnotherProcessAnswersItAsync()
    {
        FakeAcpAgent fake = Fake(0m, FakeAcpTurn.Ask("which database?") with { Tools = [new FakeAcpTool("shell", "ls -la")] }, FakeAcpTurn.Report("used it", "ready to go"));
        RunningCli run = Start([.. acpRun, "--reply-timeout", "60", "--stream"]);
        await WaitForQuestionAsync(run);
        string id = TheJob();

        CliResult said = await repo.RunAsync("jobs", "say", id, "sqlite");
        int exit = await run.WaitForExitAsync(limit, Ct);

        Assert.Equal(ExitCodes.Ok, said.ExitCode);
        Assert.True(exit == ExitCodes.Ok, run.Stderr + run.Stdout);
        string everything = run.Stderr + run.Stdout;

        // The agent streams its reply in pieces; the formatter prints a message as one line (and a tool as one line before it).
        Assert.Contains("SHELL ls -la", everything, StringComparison.Ordinal);
        Assert.Contains("SAYS  which database? ($0.010000)", everything, StringComparison.Ordinal);
        Assert.Contains("SAYS  ready to go", everything, StringComparison.Ordinal);
        Assert.Contains("($0.020000)", everything, StringComparison.Ordinal);
        Assert.Equal(1, fake.Runs);
        Assert.Equal(2, fake.Prompts);
        Assert.Equal("sqlite", fake.PromptText(2));
    }

    [Fact]
    public async Task FollowSeesEveryTurnAndEndsWithTheJobAsync()
    {
        Fake(0m, FakeAcpTurn.Ask("which database?"), FakeAcpTurn.Report("used it", "ready to go"));
        RunningCli run = Start([.. acpRun, "--reply-timeout", "60"]);
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
        Assert.Contains("SAYS  which database?", shown, StringComparison.Ordinal);
        Assert.Contains("SAYS  ready to go", shown, StringComparison.Ordinal);
        Assert.Contains("> sqlite", shown.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        // One continuous stdout.log holds both turns' frames, and the client's half is beside it.
        string log = ReadShared(Path.Combine(repo.JobsRoot, id, "stdout.log"));
        Assert.Contains("\"id\":\"prompt-5\"", log, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"prompt-6\"", log, StringComparison.Ordinal);
        string[] sent = File.ReadAllLines(Path.Combine(repo.JobsRoot, id, "acp-client.jsonl"));
        Assert.Equal(6, sent.Length);
        Assert.Contains("\"id\":\"prompt-6\"", sent[^1], StringComparison.Ordinal);
    }

    // ---- what the agent did to the tree --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileTheAgentWroteIsInTheReceiptAsAChangedFileWithItsDiffAsync()
    {
        FakeAcpAgent fake = Fake(0m, new FakeAcpTurn("Created hello.txt", Summary: "added hello.txt", Writes: [("hello.txt", "hi\n")]));

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync([.. acpRun[4..], "--no-live"]);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        Assert.Equal("success", Str(result, "status"));
        JsonElement changed = Assert.Single(result.GetProperty("changed_files").EnumerateArray());
        Assert.Equal("hello.txt", Str(changed, "path"));
        Assert.Contains("+hi", Str(result, "diff"), StringComparison.Ordinal);
        Assert.Equal("added hello.txt", result.GetProperty("report").GetProperty("data").GetProperty("summary").GetString());
        Assert.Equal(Path.GetFullPath(repo.Repo), Path.GetFullPath(fake.Cwd(1)));
    }

    // An isolated run (cast max_parallel 2) is cut its own worktree: the agent's session is opened in it, its file lands on the job's branch as a commit, and the main tree is untouched.
    [Fact]
    public async Task AnIsolatedAcpRunOpensItsSessionInItsWorktreeAndItsWorkIsACommitOnItsBranchAsync()
    {
        FakeAcpAgent fake = Fake(0m, new FakeAcpTurn("Created hello.txt", Summary: "added hello.txt", Writes: [("hello.txt", "hi\n")]));
        repo.WriteCast(maxParallel: 2);

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync([.. acpRun[4..], "--no-live"]);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        string jobId = Str(result, "job_id");
        string tail = IsolatedRepo.WorktreeTail(jobId);
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal($"claustrum/{jobId}", Str(result, "branch"));
        Assert.EndsWith(tail, Str(result, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(tail, fake.Cwd(1), StringComparison.OrdinalIgnoreCase);
        using JsonDocument open = JsonDocument.Parse(fake.Received[1]);
        Assert.EndsWith(tail, open.RootElement.GetProperty("params").GetProperty("cwd").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestGit.RevParse(repo.Repo, $"refs/heads/claustrum/{jobId}"), Str(result, "commit"));
        Assert.False(File.Exists(Path.Combine(repo.Repo, "hello.txt")));
        Assert.Equal("hi\n", File.ReadAllText(Path.Combine(repo.WorktreePath(jobId), "hello.txt")));
    }

    // ---- needs_input and `jobs answer` ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAcpJobThatNeedsInputIsAnsweredThroughSessionLoadBilledOnlyForWhatTheContinuationAddedAsync()
    {
        FakeAcpAgent first = Fake(0m, FakeAcpTurn.NeedsInput(Asks, costUsd: 0.10m));
        (CliResult firstRun, JsonElement asked) = await repo.RunBuilderAsync([.. acpRun[4..], "--no-live"]);
        string id = Str(asked, "job_id");
        Assert.Equal(ExitCodes.NeedsInput, firstRun.ExitCode);
        Assert.Equal(0.10m, asked.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(FakeAcpAgent.Session, Str(asked, "session_id"));
        Assert.Equal("acp-opencode", Str(asked, "backend"));
        Assert.Equal(["acp"], first.Argv(1));

        // The session already costs 0.10 (the first agent's total); the continuation's agent starts there and adds 0.05.
        FakeAcpAgent again = Fake(0.10m, FakeAcpTurn.Report("finished", costUsd: 0.05m));
        CliResult answer = await repo.RunAsync("jobs", "answer", id, "sqlite", "--json");

        Assert.True(answer.ExitCode == ExitCodes.Ok, answer.Stderr + answer.Stdout);
        using JsonDocument document = JsonDocument.Parse(answer.Stdout);
        Assert.Equal("success", Str(document.RootElement, "status"));
        Assert.Equal(0.05m, document.RootElement.GetProperty("cost_usd").GetDecimal());
        Assert.Equal("acp-opencode", Str(document.RootElement, "backend"));
        Assert.Equal("opencode/fake", Str(document.RootElement, "model"));

        // `acp` again, the session loaded rather than opened, the pinned model set once more, and the answer as the first prompt.
        Assert.Equal(["acp"], again.Argv(1));
        Assert.Equal(["initialize", "session/load", "session/set_config_option", "session/set_config_option", "session/prompt"], again.Methods);
        using JsonDocument load = JsonDocument.Parse(again.Received[1]);
        Assert.Equal(FakeAcpAgent.Session, load.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(["model=opencode/fake", "mode=claustrum-builder"], again.ConfigOptions);
        Assert.Contains("sqlite", again.PromptText(1), StringComparison.Ordinal);

        // The export before and after: 0.10 then 0.15.
        Assert.Equal(2, again.Exports);
    }

    // ---- a refusal in the handshake, a timeout -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AModelTheAgentRefusesIsAFailedReceiptOfKindModelNotFoundAtNoCostBeforeAnyWorkAsync()
    {
        FakeAcpAgent fake = Fake(0m, FakeAcpTurn.Report("never reached"));
        fake.Refuse("session/set_config_option", new FakeAcpError(-32602, "Invalid params: model not found: opencode/fake", ModelId: "opencode/fake"), configId: "model");

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync([.. acpRun[4..], "--no-live"]);

        Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
        Assert.Equal("failed", Str(result, "status"));
        Assert.Equal("model_not_found", Str(result, "error_kind"));
        Assert.Equal("Invalid params: model not found: opencode/fake", Str(result, "error_detail"));
        Assert.Equal(0m, result.GetProperty("cost_usd").GetDecimal());
        Assert.True(result.GetProperty("before_any_work").GetBoolean());
        Assert.Equal(0, fake.Prompts);
        Assert.Equal(0, fake.Exports);
        Assert.Equal(["model=opencode/fake"], fake.ConfigOptions);
    }

    [Fact]
    public async Task AModelWithABadVariantIsAFailedReceiptOfKindOtherNamingTheEffortOptionAsync()
    {
        FakeAcpAgent fake = Fake(0m, FakeAcpTurn.Report("never reached"));
        fake.Refuse("session/set_config_option", new FakeAcpError(-32602, "Invalid params: effort not found: bogus"), configId: "effort");

        (_, JsonElement result) = await repo.RunBuilderAsync("--backend", "acp-opencode", "--model", "opencode/fake#bogus", "--no-live");

        Assert.Equal("failed", Str(result, "status"));
        Assert.Equal("other", Str(result, "error_kind"));
        Assert.Equal("acp-opencode", Str(result, "backend"));
        Assert.Equal(0m, result.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(["model=opencode/fake", "effort=bogus"], fake.ConfigOptions);
    }

    [Fact]
    public async Task ATimeoutMidTurnIsATimeoutReceiptWithTheCostFromTheExportAndNoOrphanAsync()
    {
        FakeAcpAgent fake = Fake(0m, new FakeAcpTurn("working on it", StallSeconds: 120, Tools: [new FakeAcpTool("shell", "sleep 120", Status: "in_progress")]));

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync([.. acpRun[4..], "--no-live", "--timeout", "3"]);

        Assert.Equal(ExitCodes.Timeout, process.ExitCode);
        Assert.Equal("timeout", Str(result, "status"));
        Assert.Equal(0.01m, result.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(1, fake.Exports);
        await LiveJobHarness.WaitUntilAsync(() => !FakeAcpAgent.IsRunning(fake.Pid(1)) && (fake.NapPid is not { } nap || !FakeAcpAgent.IsRunning(nap)), "the agent and its sleep to be gone");
    }

    // ---- the options a run carries ---------------------------------------------------------------------------------------------------

    // The default cast of this class's repository puts the builder on claude, and a cast's backend wins over a prefix in `--model`; without it the prefix is what chooses.
    [Fact]
    public async Task TheAliasSpellingAndAVariantReachTheAgentAsModelEffortAndModeInThatOrderAsync()
    {
        FakeAcpAgent fake = Fake(0m, FakeAcpTurn.Report("plain"));
        File.Delete(Path.Combine(repo.Repo, ".claustrum", "casts", "default.json"));

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--model", "acp-opencode:opencode/fake#high", "--no-live");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal("acp-opencode", Str(result, "backend"));
        Assert.Equal("opencode/fake#high", Str(result, "model"));
        Assert.Equal(["model=opencode/fake", "effort=high", "mode=claustrum-builder"], fake.ConfigOptions);
        Assert.Equal(["acp"], fake.Argv(1));
    }

    // ---- the harness refusal and the doctor -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClaudeOnlyRoleOnAcpOpencodeExitsTwoNamingBothNamesAndMintsNoJobAsync()
    {
        Fake(0m, FakeAcpTurn.Report("never reached"));

        CliResult result = await repo.RunAsync("run", "ui-reviewer", "--backend", "acp-opencode", "--brief", "x");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("role 'ui-reviewer' runs on claude only", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'acp-opencode' (harness 'opencode')", result.Stderr, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
    }

    [Fact]
    public async Task DoctorFindsTheConfiguredBinaryForTheAcpBackendAndReportsItsVersionWithNoAdvisoryAsync()
    {
        FakeAcpAgent fake = Fake(0m, FakeAcpTurn.Report("x"));

        CliResult result = await repo.RunAsync("backends", "doctor", "acp-opencode");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("acp-opencode:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("found:   True", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"path:    {fake.ScriptPath}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("version: fake-acp 1.0.0", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("advisory:", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("acp-opencode", result.Stdout[result.Stdout.IndexOf("merged config:", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNativeOpencodeBackendIsUntouchedByTheAcpOnesPathAsync()
    {
        Fake(0m, FakeAcpTurn.Report("x"));

        CliResult result = await repo.RunAsync("backends", "doctor", "opencode");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("version: fake-acp", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("found:   False", result.Stdout, StringComparison.Ordinal);
    }
}
