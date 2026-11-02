using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Tracing;

// #145: the tool calls whose error text, not output, says the environment refused them: opencode's `Permission denied: <permission>`,
// claude's permission_denied events and denials, a missing tool, plan mode, a fetch answered 401/403/404, a read of a path that is gone.
// Shapes are those of the recorded jobs of 2026-10-29 (acp-opencode code-reviewer and builder runs, a claude ui-reviewer at shell); the paths
// and texts are invented.
public sealed class EnvironmentClassifierToolTests
{
    private const string Worktree = "/home/me/repos/odoo-altus/.claustrum/worktrees/20261029-092218-7dd49492";
    private const string Main = "/home/me/repos/odoo-altus";
    private const string External = "Permission denied: external_directory";

    private static RunLogs.ToolCall Refused(string kind, object input) => RunLogs.Failed(kind, input, External);

    // ---- external_directory (opencode and acp-opencode) -------------------------------------------------------------------------------------

    [Fact]
    public void ARefusedReadOrSearchNamesThePathItWasGiven()
    {
        string log = RunLogs.Acp(
            Refused("read", new { path = Main, limit = 1 }),
            Refused("search", new { pattern = "def portal_my_invoices", path = "/home/me/repos/odoo/shared/odoo-19.0", include = "*.py" }),
            Refused("read", new { path = Main, limit = 1 }));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly, cwd: Worktree));

        Assert.Equal(EnvironmentIssueKind.ExternalDirectory, issue.Kind);
        Assert.Equal(External, issue.What);
        Assert.Equal($"{Main}, /home/me/repos/odoo/shared/odoo-19.0", issue.Where);
        Assert.Equal(3, issue.Count);
        Assert.Equal("stdout.log:5", issue.First);
    }

    // A shell call names the outside path in its command, since its cwd is the job's own: the first one outside the cwd.
    [Fact]
    public void ARefusedShellCallNamesTheFirstPathOutsideTheCwdInItsCommand()
    {
        string command = $"cd {Main} && git log --all -p -S \"_altus_compute_child_name\" -- addons/altus_crm_import/models/project_task.py";
        string log = RunLogs.Acp(new RunLogs.ToolCall("shell", new { command, cwd = Worktree }, External, IsError: true, Title: command));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell, cwd: Worktree));

        Assert.Equal(EnvironmentIssueKind.ExternalDirectory, issue.Kind);
        Assert.Equal(Main, issue.Where);
    }

    [Fact]
    public void ARefusedShellCallWithNoPathOutsideFallsBackToItsWorkdirThenItsCommand()
    {
        string log = RunLogs.Opencode(
            new RunLogs.ToolCall("shell", new { command = "make test", workdir = "/srv/elsewhere" }, External, IsError: true),
            new RunLogs.ToolCall("shell", new { command = "make lint", workdir = Worktree }, External, IsError: true));

        EnvironmentIssue[] issues = RunLogs.ClassifyOpencode(log, cwd: Worktree);

        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal("/srv/elsewhere, make lint", issue.Where);
        Assert.Equal(2, issue.Count);
    }

    [Fact]
    public void MoreThanFiveWheresAreCappedWithAPlusNMoreAndTheCountStaysPerCall()
    {
        RunLogs.ToolCall[] calls = [.. Enumerable.Range(1, 7).Select(n => Refused("read", new { path = $"/srv/shared/lib{n}", limit = 1 }))];
        RunLogs.ToolCall repeat = Refused("read", new { path = "/srv/shared/lib1", limit = 1 });

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(RunLogs.Acp([.. calls, repeat]), PermissionLevel.ReadOnly, cwd: Worktree));

        Assert.Equal("/srv/shared/lib1, /srv/shared/lib2, /srv/shared/lib3, /srv/shared/lib4, /srv/shared/lib5 +2 more", issue.Where);
        Assert.Equal(8, issue.Count);
    }

    [Fact]
    public void ExactlyFiveWheresHaveNoMoreSuffix()
    {
        RunLogs.ToolCall[] calls = [.. Enumerable.Range(1, 5).Select(n => Refused("read", new { path = $"/srv/shared/lib{n}", limit = 1 }))];

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(RunLogs.Acp(calls), PermissionLevel.ReadOnly, cwd: Worktree));

        Assert.DoesNotContain("more", issue.Where, StringComparison.Ordinal);
    }

    // opencode reports its child sessions' calls under the run's own session id; another session's updates are not this run's.
    [Fact]
    public void AChildSessionsCallUnderTheRunsOwnSessionIsCountedAndAnotherSessionsIsNot()
    {
        string log = RunLogs.Acp(Refused("read", new { path = Main, limit = 1 }));
        string foreign = log.Replace("\"sessionId\":\"ses_1\",\"update\"", "\"sessionId\":\"ses_other\",\"update\"", StringComparison.Ordinal);
        string child = log.Replace("\"toolCallId\":\"call_0\"", "\"toolCallId\":\"ses_child:call_0\"", StringComparison.Ordinal);

        Assert.Empty(RunLogs.ClassifyAcp(foreign, PermissionLevel.ReadOnly, cwd: Worktree));
        Assert.Single(RunLogs.ClassifyAcp(child, PermissionLevel.ReadOnly, cwd: Worktree));
    }

    [Fact]
    public void UpdatesBeforeTheSessionIsOpenedAreNotThisRunsWork()
    {
        string log = RunLogs.Acp(Refused("read", new { path = Main, limit = 1 }));
        string[] lines = log.Split('\n');
        string replayed = string.Join('\n', [lines[0], lines[2], lines[3], lines[4], lines[1], lines[^2], lines[^1]]);

        Assert.Empty(RunLogs.ClassifyAcp(replayed, PermissionLevel.ReadOnly, cwd: Worktree));
    }

    [Fact]
    public void OpencodesNativeToolUseErrorIsReadLikeAnAcpOne()
    {
        string log = RunLogs.Opencode(
            RunLogs.Failed("read", new { filePath = Main }, External),
            RunLogs.Failed("read", new { filePath = $"{Worktree}/.env" }, "Permission denied: read"),
            RunLogs.Failed("read", new { filePath = $"{Worktree}/secrets.txt" }, "Permission denied: read"),
            RunLogs.Failed("question", new { questions = "x" }, "Permission denied: question"));

        EnvironmentIssue[] issues = RunLogs.ClassifyOpencode(log, cwd: Worktree);

        Assert.Equal([EnvironmentIssueKind.ExternalDirectory, EnvironmentIssueKind.PermissionDenied], issues.Select(issue => issue.Kind));
        Assert.Equal("Permission denied: read", issues[1].What);
        Assert.Equal($"{Worktree}/secrets.txt", issues[1].Where);
    }

    // ---- permission_denied: opencode's own refusals ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("read", "Permission denied: read", "/work/repo/.env")]
    [InlineData("read", "Permission denied: read", "/work/repo/.env.local")]
    [InlineData("read", "Permission denied: read", "/work/repo/config/prod.env")]
    [InlineData("question", "Permission denied: question", "")]
    public void WhatTheHeadlessGuardsRefuseOnPurposeIsNotAnIssue(string kind, string error, string path)
    {
        string log = RunLogs.Acp(RunLogs.Failed(kind, new { filePath = path }, error));

        Assert.Empty(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell));
    }

    [Fact]
    public void AnEnvExampleIsNotAGuardedFileSoItsRefusalCounts()
    {
        string log = RunLogs.Acp(RunLogs.Failed("read", new { filePath = "/work/repo/.env.example" }, "Permission denied: read"));

        Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell));
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly, "edit", true)]
    [InlineData(PermissionLevel.Shell, "edit", true)]
    [InlineData(PermissionLevel.Edit, "edit", false)]
    [InlineData(PermissionLevel.EditShell, "edit", false)]
    [InlineData(PermissionLevel.Edit, "shell", true)]
    [InlineData(PermissionLevel.EditShell, "shell", false)]
    [InlineData(PermissionLevel.ReadOnly, "shell", false)]
    public void ARefusalOfWhatTheLevelWithholdsIsThePolicyWorkingAndTheRestIsAnIssue(PermissionLevel level, string permission, bool onPurpose)
    {
        string log = RunLogs.Acp(RunLogs.Failed("other", new { filePath = "/work/repo/x.txt" }, $"Permission denied: {permission}"));

        EnvironmentIssue[] issues = RunLogs.ClassifyAcp(log, level);

        Assert.Equal(onPurpose ? 0 : 1, issues.Length);
        if (!onPurpose)
            Assert.Equal(EnvironmentIssueKind.PermissionDenied, issues[0].Kind);
    }

    [Fact]
    public void ARefusedCommandTheRolesDenyListNamesIsThePolicyWorking()
    {
        string log = RunLogs.Acp(
            new RunLogs.ToolCall("shell", new { command = "git push origin HEAD", cwd = RunLogs.Cwd }, "Permission denied: shell", IsError: true, Title: "git push origin HEAD"),
            new RunLogs.ToolCall("shell", new { command = "git pull", cwd = RunLogs.Cwd }, "Permission denied: shell", IsError: true, Title: "git pull"));

        Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell));
        EnvironmentIssue only = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.EditShell, deny: ["git push"]));
        Assert.Equal("git pull", only.Where);
    }

    // ---- claude: permission_denied events, denials, plan mode ----------------------------------------------------------------------------

    private static string ClaudeDenied(string mode, string tool, object input, bool withResultDenial, bool withResult = true)
    {
        List<string> lines =
        [
            RunLogs.Json(new { type = "system", subtype = "init", permissionMode = mode, session_id = "s1" }),
            RunLogs.Json(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id = "toolu_1", name = tool, input } } } }),
            RunLogs.Json(new { type = "system", subtype = "permission_denied", tool_name = tool, tool_use_id = "toolu_1", message = "Permission for this tool use was denied." }),
            RunLogs.Json(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "toolu_1", content = "Permission for this tool use was denied. It requires approval.", is_error = true } } } }),
        ];
        object[] denials = withResultDenial ? [new { tool_name = tool, tool_use_id = "toolu_1", tool_input = input }] : [];
        if (withResult)
            lines.Add(RunLogs.Json(new { type = "result", subtype = "success", result = "done", permission_denials = denials }));

        return string.Join('\n', lines) + "\n";
    }

    [Fact]
    public void AClaudeDenialIsOneIssueWhetherTheSystemLineOrTheResultListedItAndItIsAtTheToolResultsLine()
    {
        object input = new { url = "http://localhost:8131/web/dev_login/coord.test?redirect=/my", isolatedContext = "coord" };

        foreach (bool both in new[] { true, false })
        {
            EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(ClaudeDenied("default", "mcp__chrome__new_page", input, withResultDenial: both), PermissionLevel.Shell));

            Assert.Equal(EnvironmentIssueKind.PermissionDenied, issue.Kind);
            Assert.Equal("Permission denied: mcp__chrome__new_page", issue.What);
            Assert.Equal("http://localhost:8131/web/dev_login/coord.test?redirect=/my", issue.Where);
            Assert.Equal(1, issue.Count);
            Assert.Equal("stdout.log:4", issue.First);
        }
    }

    // A run cut before its `result` keeps the denial: the system line is said when it happens.
    [Fact]
    public void AClaudeDenialSurvivesARunCutBeforeItsResult()
    {
        string cut = ClaudeDenied("default", "Bash", new { command = "docker ps" }, withResultDenial: false, withResult: false);

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(cut, PermissionLevel.Shell));
        Assert.Equal("docker ps", issue.Where);
    }

    [Fact]
    public void AClaudeDenialOnlyInTheResultListIsReadToo()
    {
        string log = RunLogs.ClaudeWithDenials(
            "default",
            [new { tool_name = "WebFetch", tool_use_id = "toolu_9", tool_input = new { url = "https://example.com/x" } }],
            RunLogs.Bash("ls", "a"));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(log, PermissionLevel.Shell));

        Assert.Equal("Permission denied: WebFetch", issue.What);
        Assert.Equal("https://example.com/x", issue.Where);
        Assert.Equal("stdout.log:4", issue.First);
    }

    [Theory]
    [InlineData("Monitor", PermissionLevel.EditShell)]
    [InlineData("Write", PermissionLevel.Shell)]
    [InlineData("Edit", PermissionLevel.ReadOnly)]
    [InlineData("NotebookEdit", PermissionLevel.Shell)]
    [InlineData("Bash", PermissionLevel.Edit)]
    public void AClaudeDenialTheLevelOrTheHarnessMeansIsNotAnIssue(string tool, PermissionLevel level)
    {
        Assert.Empty(RunLogs.ClassifyClaude(ClaudeDenied("default", tool, new { command = "x", file_path = "/w/f" }, withResultDenial: true), level));
    }

    [Fact]
    public void AClaudeDenialOfACommandOnTheDenyListIsNotAnIssueButAnotherCommandIs()
    {
        string pushed = ClaudeDenied("default", "Bash", new { command = "git push origin HEAD" }, withResultDenial: true);
        string pulled = ClaudeDenied("default", "Bash", new { command = "git pull" }, withResultDenial: true);

        Assert.Empty(RunLogs.ClassifyClaude(pushed, PermissionLevel.EditShell, deny: ["git push"]));
        Assert.Single(RunLogs.ClassifyClaude(pulled, PermissionLevel.EditShell, deny: ["git push"]));
    }

    [Theory]
    [InlineData(PermissionLevel.Shell, true)]
    [InlineData(PermissionLevel.Edit, true)]
    [InlineData(PermissionLevel.EditShell, true)]
    [InlineData(PermissionLevel.Full, true)]
    [InlineData(PermissionLevel.ReadOnly, false)]
    public void PlanModeIsAnIssueForARoleMeantToRunOrEditAndNotForAReadonlyOne(PermissionLevel level, bool issue)
    {
        EnvironmentIssue[] issues = RunLogs.ClassifyClaude(RunLogs.Claude("plan", RunLogs.Bash("ls", "a")), level);

        Assert.Equal(issue ? 1 : 0, issues.Length);
        if (issue)
        {
            Assert.Equal(EnvironmentIssueKind.PlanMode, issues[0].Kind);
            Assert.Equal($"the session started in plan mode, at permission {PermissionLevelParser.ToConfigString(level)}", issues[0].What);
            Assert.Null(issues[0].Where);
            Assert.Equal("stdout.log:1", issues[0].First);
        }
    }

    [Fact]
    public void OnlyTheFirstInitSetsThePermissionModeAndAnotherModeIsNotPlan()
    {
        Assert.Empty(RunLogs.ClassifyClaude(RunLogs.Claude("acceptEdits", RunLogs.Bash("ls", "a")), PermissionLevel.EditShell));
        Assert.Empty(RunLogs.ClassifyClaude(RunLogs.Claude("dontAsk", RunLogs.Bash("ls", "a")), PermissionLevel.Shell));

        string twice = RunLogs.Claude("default") + RunLogs.Claude("plan");
        Assert.Empty(RunLogs.ClassifyClaude(twice, PermissionLevel.Shell));
    }

    // ---- missing_tool -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("No tool named \"execute\" is currently available. Did you mean 'shell'?")]
    [InlineData("No tool named \"mcp__chrome__new_page\" is currently available.")]
    public void AToolTheRoleNeededButDidNotHaveIsAMissingTool(string error)
    {
        string log = RunLogs.Acp(RunLogs.Failed("other", new { command = "gh issue view 225 --comments 2>&1 | head -200" }, error));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly));

        Assert.Equal(EnvironmentIssueKind.MissingTool, issue.Kind);
        Assert.StartsWith("tool \"", issue.What, StringComparison.Ordinal);
        Assert.EndsWith("\" is not available", issue.What, StringComparison.Ordinal);
        Assert.Equal("gh issue view 225 --comments 2>&1 | head -200", issue.Where);
    }

    [Theory]
    [InlineData("bash", "<tool_use_error>Error: No such tool available: bash. Tool names are case-sensitive: call Bash instead.</tool_use_error>", PermissionLevel.EditShell)]
    [InlineData("bash_placeholder", "<tool_use_error>Error: No such tool available: bash_placeholder</tool_use_error>", PermissionLevel.EditShell)]
    [InlineData("Write", "<tool_use_error>Error: No such tool available: Write. Write is disabled for this session, in subagents as well as here.</tool_use_error>", PermissionLevel.Shell)]
    [InlineData("Edit", "<tool_use_error>Error: No such tool available: Edit</tool_use_error>", PermissionLevel.ReadOnly)]
    [InlineData("Bash", "<tool_use_error>Error: No such tool available: Bash</tool_use_error>", PermissionLevel.Edit)]
    public void AMiscasedNameAnInventedOneAndWhatTheLevelWithholdsAreNotMissingTools(string tool, string error, PermissionLevel level)
    {
        string log = RunLogs.Claude("default", new RunLogs.ToolCall(tool, new { command = "ls" }, error, IsError: true));

        Assert.Empty(RunLogs.ClassifyClaude(log, level));
    }

    [Fact]
    public void AClaudeMcpToolThatIsNotThereIsAMissingToolAndAnEditToolAtAnEditLevelToo()
    {
        string log = RunLogs.Claude(
            "default",
            new RunLogs.ToolCall("mcp__chrome__new_page", new { url = "http://x" }, "<tool_use_error>Error: No such tool available: mcp__chrome__new_page</tool_use_error>", IsError: true),
            new RunLogs.ToolCall("Edit", new { file_path = "/w/a" }, "<tool_use_error>Error: No such tool available: Edit</tool_use_error>", IsError: true));

        EnvironmentIssue[] issues = RunLogs.ClassifyClaude(log, PermissionLevel.EditShell);

        Assert.Equal(2, issues.Length);
        Assert.All(issues, issue => Assert.Equal(EnvironmentIssueKind.MissingTool, issue.Kind));
        Assert.Equal("tool \"mcp__chrome__new_page\" is not available", issues[0].What);
    }

    // ---- fetch: a 401/403/404 on a site the job needed ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("StatusCode: non 2xx status code (404 GET https://api.github.com/repos/x/y/issues/231)", "https://api.github.com/repos/x/y/issues/231", "HTTP 404 from GitHub")]
    [InlineData("StatusCode: non 2xx status code (403 GET https://api.example.com/v1/x)", "https://api.example.com/v1/x", "HTTP 403")]
    [InlineData("Request failed with status code 401", "https://api.example.com/v1/x", "HTTP 401")]
    [InlineData("HTTP 403 Forbidden", "https://api.example.com/v1/x", "HTTP 403")]
    [InlineData("404 Not Found", "https://raw.githubusercontent.com/x/y/main/f.py", "HTTP 404 from GitHub")]
    public void AFetchAnswered401Or403OrA404OnGitHubIsAnAuthIssue(string error, string url, string what)
    {
        string log = RunLogs.Acp(RunLogs.Failed("fetch", new { url, format = "text" }, error));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly));

        Assert.Equal(EnvironmentIssueKind.Auth, issue.Kind);
        Assert.Equal(what, issue.What);
        Assert.Equal(url, issue.Where);
    }

    [Theory]
    [InlineData("StatusCode: non 2xx status code (404 GET https://example.com/missing)", "https://example.com/missing")]
    [InlineData("StatusCode: non 2xx status code (403 GET http://localhost:8131/web/login)", "http://localhost:8131/web/login")]
    [InlineData("StatusCode: non 2xx status code (500 GET https://api.github.com/x)", "https://api.github.com/x")]
    [InlineData("connection reset", "https://api.github.com/x")]
    public void A404OnAPublicSiteALoopback403AndOtherFailuresAreNotAuthIssues(string error, string url)
    {
        string log = RunLogs.Acp(RunLogs.Failed("fetch", new { url }, error));

        Assert.Empty(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly));
    }

    // ---- missing_directory for a tool call: judged by the path the call was given ----------------------------------------------------------

    [Fact]
    public void AReadOfTheVanishedCwdOrInsideAReadDirIsAMissingDirectoryAndOtherPathsAreGuesses()
    {
        string log = RunLogs.Acp(
            RunLogs.Failed("read", new { path = Worktree }, $"Error: ENOENT: no such file or directory, scandir '{Worktree}'"),
            RunLogs.Failed("search", new { path = "/srv/shared/lib" }, "No such file or directory"),
            RunLogs.Failed("read", new { path = "/srv/guessed/path" }, "No such file or directory"),
            RunLogs.Failed("read", new { path = $"{Worktree}/docs/notes.md" }, "File not found"));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly, cwd: Worktree, readDirs: ["/srv/shared"]));

        Assert.Equal(EnvironmentIssueKind.MissingDirectory, issue.Kind);
        Assert.Equal("path does not exist", issue.What);
        Assert.Equal($"{Worktree}, /srv/shared/lib", issue.Where);
        Assert.Equal(2, issue.Count);
    }

    // claude's read error names the cwd it ran in: the path judged is the one the call was given, never one its error mentions.
    [Fact]
    public void AnErrorThatNamesTheCwdButWasAboutAnotherPathIsNotMissingDirectory()
    {
        string log = RunLogs.Claude(
            "default",
            new RunLogs.ToolCall("Read", new { file_path = "/srv/guessed/f.py" }, $"File does not exist. Note: your current working directory is {Worktree}.", IsError: true));

        Assert.Empty(RunLogs.ClassifyClaude(log, PermissionLevel.EditShell, cwd: Worktree));
    }

    // ---- the shape of claude's stream: replays, live turns, and calls that never got a result -----------------------------------------------------

    [Fact]
    public void AReplayedLineIsAnotherRunsAndIsNotCounted()
    {
        string log = RunLogs.Claude("default", RunLogs.Bash("docker ps", "permission denied while trying to connect to the docker API at unix:///var/run/docker.sock", isError: true));
        string replayed = string.Join('\n', log.Split('\n').Select(line => line.Length > 0 && line.Contains("\"type\":\"user\"", StringComparison.Ordinal) ? line.Replace("{\"type\":\"user\"", "{\"isReplay\":true,\"type\":\"user\"", StringComparison.Ordinal) : line));

        Assert.Single(RunLogs.ClassifyClaude(log));
        Assert.Empty(RunLogs.ClassifyClaude(replayed));
    }

    // A live session ends a turn with a `result` each, and each lists the denials of the session so far: the same call is one denial.
    [Fact]
    public void ADenialListedByEveryTurnsResultIsCountedOnce()
    {
        object denial = new { tool_name = "WebFetch", tool_use_id = "toolu_7", tool_input = new { url = "https://example.com/x" } };
        string turn = RunLogs.ClaudeWithDenials("default", [denial], RunLogs.Bash("ls", "a"));
        string twice = turn + RunLogs.ClaudeWithDenials("default", [denial], RunLogs.Bash("ls", "a")).Split('\n', 2)[1];

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(twice, PermissionLevel.Shell));

        Assert.Equal(1, issue.Count);
    }

    [Fact]
    public void AToolUseThatNeverGotAResultIsNotAnIssue()
    {
        string[] lines =
        [
            RunLogs.Json(new { type = "system", subtype = "init", permissionMode = "default", session_id = "s1" }),
            RunLogs.Json(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id = "toolu_1", name = "Bash", input = new { command = "docker ps" } } } } }),
        ];

        Assert.Empty(RunLogs.ClassifyClaude(string.Join('\n', lines) + "\n"));
    }

    [Fact]
    public void AToolResultMadeOfTextItemsIsReadLikeAString()
    {
        string[] lines =
        [
            RunLogs.Json(new { type = "system", subtype = "init", permissionMode = "default", session_id = "s1" }),
            RunLogs.Json(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id = "toolu_1", name = "Bash", input = new { command = "docker ps" } } } } }),
            RunLogs.Json(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "toolu_1", content = new[] { new { type = "text", text = "permission denied while trying to connect to the Docker daemon socket" } }, is_error = true } } } }),
        ];

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(string.Join('\n', lines) + "\n"));

        Assert.Equal(EnvironmentIssueKind.ContainerPermission, issue.Kind);
    }

    // ---- what is never read ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AReadOrSearchOutputIsNeverScannedAndASuccessfulCallHasNoIssue()
    {
        string log = RunLogs.Claude(
            "default",
            new RunLogs.ToolCall("Read", new { file_path = "/w/docs/TROUBLESHOOTING.md" }, "Permission denied: external_directory\nPermissionError: [Errno 13] Permission denied: '/x'\nHTTP 404: Not Found"),
            new RunLogs.ToolCall("Grep", new { pattern = "Errno" }, "a.py:1: PermissionError: [Errno 13] Permission denied: '/x'"),
            new RunLogs.ToolCall("WebFetch", new { url = "https://example.com/doc" }, "HTTP 403 Forbidden is what the page documents"));

        Assert.Empty(RunLogs.ClassifyClaude(log, PermissionLevel.EditShell));
    }

    [Fact]
    public void ALogThatIsNotAnyBackendsStreamHasNoIssues()
    {
        Assert.Empty(RunLogs.ClassifyClaude("not json\n{broken\n\n[1,2]\n"));
        Assert.Empty(RunLogs.ClassifyAcp("not json\n{broken\n\n[1,2]\n"));
        Assert.Empty(RunLogs.ClassifyOpencode(""));
    }
}
