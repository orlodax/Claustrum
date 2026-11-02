using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #145 and #144 through the real built binary, against a real repository and a fake `claude` script (IsolatedRepo): a read dir that is missing is
// said at dispatch, on stderr and first in the receipt; a run whose stream shows the environment refusing it is `success_with_issues`, exit 8,
// with one stderr line in both output modes; `jobs show`, `jobs wait` and `jobs follow` read the same receipt back. Read dirs reach the claude
// argv, a repo's own are ignored with a warning, and a relative one is refused at exit 2.
public sealed class EnvironmentIssuesEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-envissues-").FullName;

    public void Dispose()
    {
        repo.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    // The user config of IsolatedRepo (every alias answered) plus these keys, spelled as JSON: the user layer is the only one that may set them.
    private string WriteUserConfig(string extraKeys) => UserConfig.Write(
        repo.Home,
        "{\"models\":{\"frontier-reasoning\":\"claude:opus\",\"frontier-coding\":\"claude:opus\",\"standard-coding\":\"claude:sonnet\",\"cheap-coding\":\"claude:haiku\",\"fast\":\"claude:haiku\"}," + extraKeys + "}");

    private static string Json(string text) => JsonSerializer.Serialize(text);

    private string Missing(string name = "no-such-dir") => Path.Combine(scratch, name);

    // ---- a missing read dir: said before the job starts ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingReadDirIsAWarningOnStderrAndAFirstDispatchIssueInTheReceiptAndTheRunExitsEightAsync()
    {
        string gone = Missing();
        WriteUserConfig($"\"read_dirs\":[{Json(gone)}]");
        repo.Script(new FakeClaudeScript { Summary = "ok" });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        Assert.Equal(8, ExitCodes.EnvironmentIssues);
        Assert.Equal("success_with_issues", Str(result, "status"));
        JsonElement issue = Assert.Single(result.GetProperty("environment_issues").EnumerateArray());
        Assert.Equal("missing_directory", Str(issue, "kind"));
        Assert.Equal("a configured read_dirs directory does not exist", Str(issue, "what"));
        Assert.Equal(gone, Str(issue, "where"));
        Assert.Equal(1, issue.GetProperty("count").GetInt32());
        Assert.Equal("dispatch", Str(issue, "first"));
        Assert.Equal($"read_dirs: {gone} does not exist; the job runs without it", result.GetProperty("warnings")[0].GetString());

        // Both on stderr: the dispatch warning as the run begins and the one environment line at the end; stdout stays the one document.
        Assert.Contains($"read_dirs: {gone} does not exist; the job runs without it", process.Stderr, StringComparison.Ordinal);
        Assert.Contains($"warning: environment issues (1): missing_directory ×1 at {gone}. Stop and tell the user before using this result.", process.Stderr, StringComparison.Ordinal);
        Assert.Equal(Str(result, "job_id"), Str(repo.ReadResult(Str(result, "job_id")), "job_id"));
        Assert.Equal("success_with_issues", Str(repo.ReadResult(Str(result, "job_id")), "status"));
    }

    [Fact]
    public async Task TheEnvironmentLineIsOnStderrInHumanModeTooAsync()
    {
        WriteUserConfig($"\"read_dirs\":[{Json(Missing())}]");
        repo.Script(new FakeClaudeScript { Summary = "ok" });

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "x");

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        Assert.Contains("warning: environment issues (1): missing_directory", process.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("warning: environment issues", process.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithNoIssueHasNoEnvironmentLineAndExitsZeroAsync()
    {
        repo.Script(new FakeClaudeScript { Summary = "ok" });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("success", Str(result, "status"));
        Assert.DoesNotContain("environment issues", process.Stderr, StringComparison.Ordinal);
        Assert.True(!result.TryGetProperty("environment_issues", out JsonElement issues) || issues.ValueKind == JsonValueKind.Null);
    }

    // ---- an issue the stream shows ----------------------------------------------------------------------------------------------------------

    private const string PlanModeInit = /*lang=json,strict*/ """{"type":"system","subtype":"init","permissionMode":"plan","session_id":"s1"}""";
    private const string DockerCall = /*lang=json,strict*/ """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"docker ps"}}]}}""";
    private const string DockerRefused =
                             /*lang=json,strict*/
                             """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"permission denied while trying to connect to the docker API at unix:///var/run/docker.sock","is_error":true}]}}""";

    [Fact]
    public async Task ABuildersStreamInPlanModeAndADockerSocketRefusalAreTwoIssuesAndExitEightAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Summary = "ok", StreamLines = [PlanModeInit, DockerCall, DockerRefused] });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        Assert.Equal("success_with_issues", Str(result, "status"));
        JsonElement[] issues = [.. result.GetProperty("environment_issues").EnumerateArray()];
        Assert.Equal(["plan_mode", "container_permission"], issues.Select(issue => Str(issue, "kind")));
        Assert.Equal(["stdout.log:1", "stdout.log:3"], issues.Select(issue => Str(issue, "first")));
        Assert.Equal("docker ps", Str(issues[1], "where"));
        Assert.Contains("warning: environment issues (2): plan_mode ×1; container_permission ×1 at docker ps.", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportsBlockedByIsAnIssueWithFirstReportAndATestGateRedIsTooAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript
        {
            ReportJson = /*lang=json,strict*/ """{"status":"partial","blocked_by":[{"what":"gh not logged in","where":"gh issue view 12"}],"failed":[{"test":"T1","fault_in":"code"}]}""",
        });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        JsonElement[] issues = [.. result.GetProperty("environment_issues").EnumerateArray()];
        Assert.Equal(["test_gate_red", "reported_blocker"], issues.Select(issue => Str(issue, "kind")));
        Assert.Equal(["report", "report"], issues.Select(issue => Str(issue, "first")));
        Assert.Equal("gh issue view 12", Str(issues[1], "where"));
    }

    // ---- the same receipt read back ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task JobsShowWaitAndFollowReadTheReceiptBackAndPrintTheEnvironmentLineAsync()
    {
        WriteUserConfig($"\"read_dirs\":[{Json(Missing())}]");
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        (_, JsonElement result) = await repo.RunBuilderAsync();
        string jobId = Str(result, "job_id");

        CliResult show = await repo.RunAsync("jobs", "show", jobId);
        CliResult wait = await repo.RunAsync("jobs", "wait", jobId, "--json");
        CliResult follow = await repo.RunAsync("jobs", "follow", jobId);
        CliResult list = await repo.RunAsync("jobs", "list");

        Assert.Equal(ExitCodes.Ok, show.ExitCode);
        using JsonDocument shown = JsonDocument.Parse(show.Stdout);
        Assert.Equal("success_with_issues", Str(shown.RootElement, "status"));
        Assert.Equal("missing_directory", Str(shown.RootElement.GetProperty("environment_issues")[0], "kind"));
        Assert.Contains("warning: environment issues (1): missing_directory", show.Stderr, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Ok, wait.ExitCode);
        using JsonDocument waited = JsonDocument.Parse(wait.Stdout);
        Assert.Equal("done", Str(waited.RootElement, "state"));
        Assert.Equal("success_with_issues", Str(waited.RootElement, "result_status"));
        Assert.Equal("dispatch", Str(waited.RootElement.GetProperty("environment_issues")[0], "first"));
        Assert.Contains("warning: environment issues (1): missing_directory", wait.Stderr, StringComparison.Ordinal);

        Assert.Contains("warning: environment issues (1): missing_directory", follow.Stderr, StringComparison.Ordinal);
        Assert.Contains("success_with_issues", list.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobsWaitJsonHasANullEnvironmentIssuesForACleanJobAsync()
    {
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        (_, JsonElement result) = await repo.RunBuilderAsync();

        CliResult wait = await repo.RunAsync("jobs", "wait", Str(result, "job_id"), "--json");

        using JsonDocument waited = JsonDocument.Parse(wait.Stdout);
        Assert.Equal("success", Str(waited.RootElement, "result_status"));
        Assert.True(!waited.RootElement.TryGetProperty("environment_issues", out JsonElement issues) || issues.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("environment issues", wait.Stderr, StringComparison.Ordinal);
    }

    // The status column grew from 16 to 19 characters so `success_with_issues` fits: every row's role and backend still start in one column.
    [Fact]
    public async Task JobsListKeepsItsColumnsAlignedForTheLongestStatusAsync()
    {
        repo.Script(new FakeClaudeScript { Summary = "ok" });
        await repo.RunBuilderAsync();
        WriteUserConfig($"\"read_dirs\":[{Json(Missing())}]");
        await repo.RunBuilderAsync();

        CliResult list = await repo.RunAsync("jobs", "list");

        string[] rows = [.. list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(row => row.Contains("builder/claude", StringComparison.Ordinal))];
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.Contains("success_with_issues builder/claude", StringComparison.Ordinal));
        Assert.Single(rows.Select(row => row.IndexOf("builder/claude", StringComparison.Ordinal)).Distinct());
    }

    // ---- read dirs reach the claude argv ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConfiguredReadDirIsAnAddDirAndAnEditDenyAtTheBuildersEditShellLevelAsync()
    {
        FakeClaude.RequirePosixShell();
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        WriteUserConfig($"\"read_dirs\":[{Json(shared)}]");
        repo.Script(new FakeClaudeScript { Summary = "ok", ArgvLog = repo.ArgvLog });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("success", Str(result, "status"));
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal(shared, argv[Array.IndexOf(argv, "--add-dir") + 1]);
        Assert.Contains($"Edit(/{shared}/**)", argv);
        Assert.DoesNotContain("read_dirs", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepoThatSetsReadDirsIsIgnoredWithAWarningNamingTheFileAndNothingIsGivenToTheJobAsync()
    {
        FakeClaude.RequirePosixShell();
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{\"claude\":{\"path\":" + Json(repo.ScriptPath) + "}},\"read_dirs\":[" + Json(shared) + "]}");
        repo.Script(new FakeClaudeScript { Summary = "ok", ArgvLog = repo.ArgvLog });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.DoesNotContain("--add-dir", File.ReadAllLines(repo.ArgvLog));
        string warning = result.GetProperty("warnings").EnumerateArray().Select(entry => entry.GetString() ?? "").Single(text => text.Contains("read_dirs and projects", StringComparison.Ordinal) || text.Contains("read_dirs ignored", StringComparison.Ordinal));
        Assert.Contains("claustrum.json", warning, StringComparison.Ordinal);
        Assert.Contains("never from a repo", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"read_dirs\":[\"relative/dir\"]", "read_dirs[0]")]
    [InlineData("\"projects\":{\"claustrum-*\":{\"read_dirs\":[\"../up\"]}}", "projects.claustrum-*.read_dirs[0]")]
    [InlineData("\"projects\":{\"nope\":null}", "projects.nope is null")]
    [InlineData("\"projects\":{\"*\":{\"worktree_hook\":[\"bin/fix\"]}}", "projects.*.worktree_hook[0]")]
    public async Task ABadReadDirsOrHookEntryIsRefusedAtExitTwoNamingTheKeyWhateverTheProjectAsync(string keys, string named)
    {
        WriteUserConfig(keys);
        repo.Script(new FakeClaudeScript { Summary = "ok" });

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "x", "--json");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(named, process.Stderr, StringComparison.Ordinal);
        Assert.Contains(UserConfig.PathFor(repo.Home), process.Stderr, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
    }

    // ---- an isolated run: the git common dir for free, and the worktree hook ------------------------------------------------------------------

    [Fact]
    public async Task AnIsolatedRunReadsTheMainCheckoutsGitDirectoryAndNoKeyIsNeededAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "ok", ArgvLog = repo.ArgvLog });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.NotEqual(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        string commonDir = Path.Combine(repo.Repo, ".git");
        Assert.Equal(commonDir, argv[Array.IndexOf(argv, "--add-dir") + 1], ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task AInPlaceRunInTheMainCheckoutGetsNoExtraDirAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Summary = "ok", ArgvLog = repo.ArgvLog });

        (CliResult process, _) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.DoesNotContain("--add-dir", File.ReadAllLines(repo.ArgvLog));
    }

    [Fact]
    public async Task AWorktreeHookRunsOnAnIsolatedRunWithTheWorktreePathAndNotOnAnInPlaceOneAsync()
    {
        FakeClaude.RequirePosixShell();
        string marker = Path.Combine(scratch, "hook.marker");
        WriteUserConfig("\"projects\":{\"claustrum-cli-*\":{\"worktree_hook\":[\"sh\",\"-c\",\"printf '%s|%s' \\\"$1\\\" \\\"$PWD\\\" >> " + marker + "\",\"sh\",\"{worktree}\"]}}");
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "ok" });

        (CliResult inPlace, _) = await repo.RunBuilderAsync();
        Assert.Equal(ExitCodes.Ok, inPlace.ExitCode);
        Assert.False(File.Exists(marker), "a run that is not isolated has no worktree and runs no hook");

        repo.WriteCast(maxParallel: 2);
        (CliResult isolated, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.Ok, isolated.ExitCode);
        Assert.Equal("success", Str(result, "status"));
        string[] ran = File.ReadAllText(marker).Split('|');
        Assert.EndsWith(IsolatedRepo.WorktreeTail(Str(result, "job_id")), ran[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ran[0], ran[1], ignoreCase: true);
    }

    [Fact]
    public async Task AFailingWorktreeHookIsAnIssueAndAWarningAndTheJobRunsAnywayAsync()
    {
        FakeClaude.RequirePosixShell();
        WriteUserConfig("\"projects\":{\"*\":{\"worktree_hook\":[\"sh\",\"-c\",\"echo no selinux here >&2; exit 3\"]}}");
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")], Summary = "ok" });

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.Equal(ExitCodes.EnvironmentIssues, process.ExitCode);
        Assert.Equal("success_with_issues", Str(result, "status"));
        JsonElement issue = Assert.Single(result.GetProperty("environment_issues").EnumerateArray());
        Assert.Equal("worktree_hook_failed", Str(issue, "kind"));
        Assert.Equal("exited 3: no selinux here", Str(issue, "what"));
        Assert.Equal("dispatch", Str(issue, "first"));
        Assert.Contains("worktree_hook failed: sh -c echo no selinux here >&2; exit 3: exited 3: no selinux here; the job runs anyway", result.GetProperty("warnings").EnumerateArray().Select(entry => entry.GetString()));
        Assert.Equal("first.txt", Assert.Single(result.GetProperty("changed_files").EnumerateArray()).GetProperty("path").GetString());
    }

    // The git common dir is not given where it would be writable: copilot and cursor at the edit levels grant a read dir read-write.
    [Fact]
    public async Task AReadDirTheBackendCanOnlyGrantReadWriteIsSaidAtDispatchAsync()
    {
        FakeClaude.RequirePosixShell();
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        WriteUserConfig($"\"read_dirs\":[{Json(shared)}]");

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "x", "--json", "--backend", "copilot");

        Assert.Contains($"read_dirs: copilot grants read_dirs read-write at level edit+shell: {shared}", process.Stderr, StringComparison.Ordinal);
    }
}
