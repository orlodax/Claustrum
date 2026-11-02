using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Tracing;

// #145: whole runs, shaped like the four jobs the issue was found in (acp-opencode code-reviewer 20261029-094020-089b47aa, a failed one
// 20261029-095003-002baa0d, a builder 20261029-092218-3172ff82, and claude's ui-reviewer 20261029-095220-0da9d76d), at the same permission levels and
// with the same kinds and counts of call. They are SYNTHETIC: the logs were not copied out of the job store into the repository (a private store; the
// copy was declined by the permission layer in the pass that wrote these), so the paths and texts are invented and only the shapes are recorded ones.
// The classifier was also run over the real logs by hand in that pass, read-only, and gave these same kinds and counts.
public sealed class EnvironmentClassifierJobScenariosTests
{
    private const string Main = "/home/me/repos/odoo/odoo-altus";
    private const string Shared = "/home/me/repos/odoo/shared";
    private const string External = "Permission denied: external_directory";

    private static RunLogs.ToolCall Refused(string kind, object input) => RunLogs.Failed(kind, input, External);

    private static RunLogs.ToolCall Fetched404(string url) =>
        RunLogs.Failed("fetch", new { url, format = "text" }, $"StatusCode: non 2xx status code (404 GET {url})");

    [Fact]
    public void AReadonlyReviewerThatCouldNotReadTheMainCheckoutNorGitHubIsSuccessWithIssues()
    {
        string cwd = $"{Main}/.claustrum/worktrees/20261029-092218-7dd49492";
        string log = RunLogs.Acp(
            Refused("read", new { path = Main, limit = 1 }),
            Refused("search", new { pattern = "def portal_my_invoices", path = $"{Shared}/odoo-19.0", include = "*.py" }),
            Refused("search", new { pattern = "def my_subscription", path = Shared, include = "*.py" }),
            Fetched404("https://api.github.com/repos/teksistemi-software/odoo-altus/issues/231"),
            Fetched404("https://api.github.com/repos/teksistemi-software/odoo-altus/issues/231/comments"),
            Fetched404("https://api.github.com/repos/teksistemi-software/odoo-altus/pulls?head=teksistemi-software:claustrum/20261029-092218-7dd49492"),
            new RunLogs.ToolCall("read", new { path = $"{cwd}/addons/x.py" }, "line 1\nline 2"));

        EnvironmentIssue[] issues = RunLogs.ClassifyAcp(log, PermissionLevel.ReadOnly, cwd: cwd);

        Assert.Equal([EnvironmentIssueKind.ExternalDirectory, EnvironmentIssueKind.Auth], issues.Select(issue => issue.Kind));
        Assert.Equal($"{Main}, {Shared}/odoo-19.0, {Shared}", issues[0].Where);
        Assert.Equal(3, issues[0].Count);
        Assert.Equal("HTTP 404 from GitHub", issues[1].What);
        Assert.Equal(3, issues[1].Count);
        Assert.StartsWith("https://api.github.com/repos/teksistemi-software/odoo-altus/issues/231,", issues[1].Where, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReviewerKeepsItsFailureAndListsEverythingThatGotInItsWay()
    {
        string cwd = $"{Main}/.claustrum/worktrees/20261029-094212-28bbc132";
        List<RunLogs.ToolCall> calls =
        [
            RunLogs.Failed("other", new { command = "gh issue view 225 --comments 2>&1 | head -200" }, "No tool named \"execute\" is currently available."),
            RunLogs.Failed("other", new { command = $"cd {cwd} && git diff 1879b47..54bcfa9 --stat" }, "No tool named \"execute\" is currently available."),
            Fetched404("https://api.github.com/repos/teksistemi-software/odoo-altus/issues/225"),
            Fetched404("https://api.github.com/repos/teksistemi-software/odoo-altus/issues/225/comments"),
            Fetched404("https://github.com/teksistemi-software/odoo-altus/issues/225"),
            Fetched404("https://raw.githubusercontent.com/teksistemi-software/odoo-altus/54bcfa9/README.md"),
            .. Enumerable.Range(1, 8).Select(n => Refused("read", new { path = $"{Main}/.git/worktrees/20261029-094212-28bbc132/file{n}" })),
            // The agent guessing paths inside its own tree: not the environment's doing.
            RunLogs.Failed("read", new { path = $"{cwd}/.git/HEAD" }, "File not found"),
            RunLogs.Failed("read", new { path = $"{cwd}/addons/altus_crm_import" }, "No such file or directory"),
            RunLogs.Failed("search", new { path = $"{cwd}/addons/altus_crm_import", pattern = "x" }, "No such file or directory"),
            RunLogs.Failed("read", new { path = $"{cwd}/addons" }, "No such file or directory"),
            // And then the working directory itself is gone.
            RunLogs.Failed("search", new { path = cwd, pattern = "x" }, "No such file or directory"),
            RunLogs.Failed("read", new { path = cwd }, "ENOENT: no such file or directory"),
            RunLogs.Failed("search", new { path = cwd, pattern = "y" }, "No such file or directory"),
        ];

        EnvironmentIssue[] issues = RunLogs.ClassifyAcp(RunLogs.Acp([.. calls]), PermissionLevel.ReadOnly, cwd: cwd);

        Assert.Equal(
            [EnvironmentIssueKind.MissingTool, EnvironmentIssueKind.Auth, EnvironmentIssueKind.ExternalDirectory, EnvironmentIssueKind.MissingDirectory],
            issues.Select(issue => issue.Kind));
        Assert.Equal(2, issues[0].Count);
        Assert.Equal(4, issues[1].Count);
        Assert.Equal(8, issues[2].Count);
        Assert.Equal(3, issues[3].Count);
        Assert.Equal(cwd, issues[3].Where);
    }

    [Fact]
    public void ABuildersOutsideShellCallsNameTheMainCheckoutAndItsFailedEditsAreNotIssues()
    {
        string cwd = $"{Main}/.claustrum/worktrees/20261029-092218-3172ff82";
        List<RunLogs.ToolCall> calls =
        [
            .. Enumerable.Range(1, 5).Select(n => new RunLogs.ToolCall(
                "shell",
                new { command = $"cd {Main} && git log --all -p -S \"needle{n}\" -- addons/", cwd },
                External,
                IsError: true,
                Title: $"Git log {n}")),
            .. Enumerable.Range(1, 13).Select(n => Refused("read", new { path = $"{Shared}/odoo-19.0/addons/mail/models/file{n}.py", limit = 1 })),
            RunLogs.Failed("edit", new { path = $"{cwd}/addons/altus_crm_import/models/project_task.py", oldString = "x", newString = "y" }, "Could not find oldString in the file"),
            RunLogs.Failed("edit", new { path = $"{cwd}/addons/altus_crm_import/models/other.py", oldString = "x", newString = "y" }, "oldString not found in content"),
        ];

        EnvironmentIssue[] issues = RunLogs.ClassifyAcp(RunLogs.Acp([.. calls]), PermissionLevel.EditShell, cwd: cwd);

        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal(EnvironmentIssueKind.ExternalDirectory, issue.Kind);
        Assert.Equal(18, issue.Count);
        Assert.StartsWith($"{Main}, {Shared}/odoo-19.0/addons/mail/models/file1.py", issue.Where, StringComparison.Ordinal);
        Assert.EndsWith(" +9 more", issue.Where, StringComparison.Ordinal);
    }

    // claude's ui-reviewer at `shell` started in plan mode (#140) and its browser calls were refused: the denial is at the tool_result's line, found
    // again in the final result's list, and still there when the run is cut before its `result`.
    [Fact]
    public void AClaudeUiReviewerInPlanModeWhoseBrowserCallWasRefusedHasTwoIssuesEvenWhenCutShort()
    {
        const string url = "http://localhost:8131/web/dev_login/coord.test?redirect=/my";
        string[] lines =
        [
            RunLogs.Json(new { type = "system", subtype = "init", permissionMode = "plan", session_id = "s1" }),
            RunLogs.Json(new { type = "assistant", message = new { content = new[] { new { type = "tool_use", id = "toolu_1", name = "mcp__chrome__new_page", input = new { url, isolatedContext = "coord" } } } } }),
            RunLogs.Json(new { type = "system", subtype = "permission_denied", tool_name = "mcp__chrome__new_page", tool_use_id = "toolu_1", message = "Cannot call mcp__chrome__new_page while in plan mode." }),
            RunLogs.Json(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "toolu_1", content = "Permission for this tool use was denied. What required approval: Cannot call mcp__chrome__new_page while in plan mode.", is_error = true } } } }),
        ];
        string cut = string.Join('\n', lines) + "\n";
        string whole = cut + RunLogs.Json(new { type = "result", subtype = "success", result = "done", permission_denials = new[] { new { tool_name = "mcp__chrome__new_page", tool_use_id = "toolu_1", tool_input = new { url } } } }) + "\n";

        foreach (string log in new[] { cut, whole })
        {
            EnvironmentIssue[] issues = RunLogs.ClassifyClaude(log, PermissionLevel.Shell, cwd: "/home/me/repos/odoo/odoo-altus-rev-231");

            Assert.Equal([EnvironmentIssueKind.PlanMode, EnvironmentIssueKind.PermissionDenied], issues.Select(issue => issue.Kind));
            Assert.Equal("stdout.log:1", issues[0].First);
            Assert.Equal("Permission denied: mcp__chrome__new_page", issues[1].What);
            Assert.Equal(url, issues[1].Where);
            Assert.Equal(1, issues[1].Count);
            Assert.Equal("stdout.log:4", issues[1].First);
        }
    }

    // The same run read as the readonly role it is not: plan mode is by design there, and the refusal of a browser tool is still the environment's.
    [Fact]
    public void ThePlanModeOfAReadonlyRoleIsByDesignButItsRefusedToolStillCounts()
    {
        string log = RunLogs.ClaudeWithDenials(
            "plan",
            [new { tool_name = "mcp__chrome__new_page", tool_use_id = "toolu_9", tool_input = new { url = "http://x" } }],
            RunLogs.Bash("git diff", "diff --git a/x b/x"));

        EnvironmentIssue issue = Assert.Single(RunLogs.ClassifyClaude(log, PermissionLevel.ReadOnly));

        Assert.Equal(EnvironmentIssueKind.PermissionDenied, issue.Kind);
    }
}
