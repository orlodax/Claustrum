using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Claude;

// Golden argv per docs/PLAN.md §A3's permission table. Every case is asserted as the *exact*
// ordered argument list ClaudeBackend.Build produces, not a subset match, so an accidental reorder
// or a dropped flag fails loudly.
public sealed class ClaudeBackendBuildTests
{
    private readonly ClaudeBackend backend = new(new FakePlatform());

    private static ResolvedRun MakeRun(
        PermissionPolicy permission,
        decimal? budget = null,
        string effort = "high",
        string? resume = null,
        bool stream = false) => new(
            Role: new ResolvedRole("builder", "system body", "claude", "sonnet", effort, permission, Blind: false, HasReport: true),
            Brief: "do the thing",
            Cwd: "/repo",
            BudgetUsd: budget,
            ResumeSession: resume,
            AttachFiles: [],
            Stream: stream,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: "/job",
            Env: []);

    [Fact]
    public void ReadOnlySetsPlanModeAndAllowedTools()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        Assert.Equal(
        [
            "-p", "--output-format", "json", "--model", "sonnet",
            "--append-system-prompt-file", "/job/system.md",
            "--permission-mode", "plan", "--permission-prompts", "none",
            "--allowedTools", "Read,Glob,Grep,Bash(git diff*),Bash(git log*),Bash(git show*),Bash(git status*),Bash(gh pr *),Bash(gh issue *)",
            "--no-session-persistence",
            "--effort", "high",
            "do the thing",
        ], spec.Args);
    }

    [Fact]
    public void EditDisallowsBash()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Edit, [])));

        Assert.Equal(
        [
            "-p", "--output-format", "json", "--model", "sonnet",
            "--append-system-prompt-file", "/job/system.md",
            "--permission-mode", "acceptEdits", "--permission-prompts", "none",
            "--disallowedTools", "Bash",
            "--no-session-persistence",
            "--effort", "high",
            "do the thing",
        ], spec.Args);
    }

    [Fact]
    public void EditShellWithDenyAppendsDisallowedTools()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, ["git push"])));

        Assert.Equal(
        [
            "-p", "--output-format", "json", "--model", "sonnet",
            "--append-system-prompt-file", "/job/system.md",
            "--permission-mode", "acceptEdits", "--permission-prompts", "none",
            "--allowedTools", "Edit,Write,Read,Glob,Grep,Bash(*)",
            "--disallowedTools", "Bash(git push*)",
            "--no-session-persistence",
            "--effort", "high",
            "do the thing",
        ], spec.Args);
    }

    [Fact]
    public void EditShellWithMultipleDenyJoinsThem()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, ["git push", "rm -rf"])));

        Assert.Contains("--disallowedTools", spec.Args);
        Assert.Contains("Bash(git push*),Bash(rm -rf*)", spec.Args);
    }

    [Fact]
    public void EditShellWithoutDenyOmitsDisallowedTools()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.DoesNotContain("--disallowedTools", spec.Args);
    }

    [Fact]
    public void FullSkipsPermissionsButStillSuppressesPrompts()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Equal(
        [
            "-p", "--output-format", "json", "--model", "sonnet",
            "--append-system-prompt-file", "/job/system.md",
            "--dangerously-skip-permissions", "--permission-prompts", "none",
            "--no-session-persistence",
            "--effort", "high",
            "do the thing",
        ], spec.Args);
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    [InlineData(PermissionLevel.Full)]
    public void EveryPermissionLevelPassesPermissionPromptsNone(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(level, [])));

        int index = Array.IndexOf(spec.Args, "--permission-prompts");
        Assert.True(index >= 0 && index + 1 < spec.Args.Length);
        Assert.Equal("none", spec.Args[index + 1]);
    }

    [Fact]
    public void BudgetIsPassedWithInvariantFormatting()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), budget: 1.5m));

        int index = Array.IndexOf(spec.Args, "--max-budget-usd");
        Assert.True(index >= 0);
        Assert.Equal("1.5", spec.Args[index + 1]);
    }

    [Fact]
    public void MissingBudgetOmitsFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), budget: null));

        Assert.DoesNotContain("--max-budget-usd", spec.Args);
    }

    [Fact]
    public void EmptyEffortOmitsFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: ""));

        Assert.DoesNotContain("--effort", spec.Args);
    }

    [Fact]
    public void ResumeIsAppendedAndNoSessionPersistenceIsOmitted()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), resume: "sess-123"));

        Assert.DoesNotContain("--no-session-persistence", spec.Args);
        int index = Array.IndexOf(spec.Args, "--resume");
        Assert.True(index >= 0);
        Assert.Equal("sess-123", spec.Args[index + 1]);
    }

    [Fact]
    public void NoResumeKeepsNoSessionPersistence()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), resume: null));

        Assert.Contains("--no-session-persistence", spec.Args);
        Assert.DoesNotContain("--resume", spec.Args);
    }

    [Fact]
    public void StreamingUsesStreamJsonAndVerbose()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), stream: true));

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], spec.Args[..4]);
    }

    [Fact]
    public void NonStreamingUsesPlainJsonFormat()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), stream: false));

        Assert.Equal(["-p", "--output-format", "json"], spec.Args[..3]);
    }

    [Fact]
    public void BriefIsTheFinalArgument()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Equal("do the thing", spec.Args[^1]);
    }

    // The rung ui-reviewer needs: run the app, never change it. ReadOnly withholds the shell it uses
    // to start a dev server; EditShell hands it write access its own role rules forbid (review
    // finding). --disallowedTools names only the write built-ins, so a Browser MCP tool stays
    // reachable — without one the role cannot do its job at all.
    [Fact]
    public void ShellKeepsPlanModeAndBlocksOnlyTheWriteTools()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Shell, [])));

        Assert.Equal("plan", spec.Args[Array.IndexOf(spec.Args, "--permission-mode") + 1]);
        Assert.Equal("Edit,Write,NotebookEdit", spec.Args[Array.IndexOf(spec.Args, "--disallowedTools") + 1]);
        Assert.DoesNotContain("--allowedTools", spec.Args);
    }

    [Fact]
    public void ShellStillAppliesTheRolesDenyPatternsToBash()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Shell, ["git push"])));

        Assert.Contains("Bash(git push*)", spec.Args);
    }
}
