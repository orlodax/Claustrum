using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Model;

namespace Claustrum.Core.Tests.Backends.Cursor;

// docs/PLAN.md §A3's cursor row, measured against a real `cursor-agent` install (NOTES.md "The
// cursor backend, validated against a real install", issues #13/#14): the whole prompt goes on
// stdin, not argv, and `-p`'s `--mode plan` is now cursor's real read-only rung.
public sealed class CursorBackendBuildTests : IDisposable
{
    private readonly CursorBackend backend = new(platform: null!); // Build never touches IPlatform.
    private readonly string systemPromptPath;

    public CursorBackendBuildTests()
    {
        string directory = Directory.CreateTempSubdirectory("claustrum-cursor-build-").FullName;
        systemPromptPath = Path.Combine(directory, "system.md");
        File.WriteAllText(systemPromptPath, "you are a reviewer");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(systemPromptPath)!, recursive: true);

    private ResolvedRun MakeRun(PermissionLevel level, string[]? deny = null, string? resume = null, string brief = "review this") => new(
        Role: new ResolvedRole("code-reviewer", "you are a reviewer", "cursor", "gpt-5", "high", new PermissionPolicy(level, deny ?? []), Blind: true, HasReport: true),
        Brief: brief,
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: resume,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: systemPromptPath,
        JobDirectory: "/job",
        Env: []);

    [Fact]
    public void BasicArgvShapeMatchesTheDocumentedFormWithNoPromptOnIt()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.EditShell));

        Assert.Equal(["-p", "--output-format", "json", "--model", "gpt-5", "-f", "--workspace", "/repo"], spec.Args);
        Assert.All(spec.Args, arg => Assert.DoesNotContain("review this", arg, StringComparison.Ordinal));
    }

    [Fact]
    public void SystemPromptAndBriefAreComposedIntoStdinNotArgv()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.EditShell));

        Assert.NotNull(spec.StdinText);
        Assert.StartsWith("you are a reviewer", spec.StdinText, StringComparison.Ordinal);
        Assert.Contains("# Task\nreview this", spec.StdinText, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyPatternsAreAppendedToTheStdinPromptAsHardRules()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.EditShell, deny: ["git push", "rm -rf"]));

        Assert.Contains("- Never run: git push", spec.StdinText, StringComparison.Ordinal);
        Assert.Contains("- Never run: rm -rf", spec.StdinText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDenyPatternsOmitsTheHardRulesSectionForEditShell()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.EditShell));

        Assert.DoesNotContain("Hard rules", spec.StdinText, StringComparison.Ordinal);
    }

    // Measured 2026-09-21: plan mode returns headless with stdin closed and blocks both an edit and
    // a shell write, while a read-only shell command still runs in it — the native rung for both.
    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    public void ReadOnlyAndShellUsePlanModeWithForce(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRun(level));

        Assert.Contains("--mode", spec.Args);
        Assert.Equal("plan", spec.Args[Array.IndexOf(spec.Args, "--mode") + 1]);
        Assert.Contains("-f", spec.Args);
    }

    [Fact]
    public void ReadOnlyStatesTheNoChangeRuleInThePromptSinceCursorHasNoFlagForIt()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.ReadOnly));

        Assert.Contains("READ-ONLY", spec.StdinText, StringComparison.Ordinal);
        Assert.Contains("Hard rules", spec.StdinText, StringComparison.Ordinal);
    }

    [Fact]
    public void EditStatesTheNoShellRuleInThePrompt()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.Edit));

        Assert.Contains("Never run a shell command", spec.StdinText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void EditAndEditShellPassForceFlagWithoutPlanMode(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRun(level));

        Assert.Contains("-f", spec.Args);
        Assert.DoesNotContain("--mode", spec.Args);
    }

    [Fact]
    public void FullPassesForceAndDisabledSandbox()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.Full));

        Assert.Contains("-f", spec.Args);
        Assert.Contains("--sandbox", spec.Args);
        Assert.Equal("disabled", spec.Args[Array.IndexOf(spec.Args, "--sandbox") + 1]);
    }

    [Fact]
    public void ResumeSessionIsAppendedLast()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.Full, resume: "sess-123"));

        Assert.Equal("--resume", spec.Args[^2]);
        Assert.Equal("sess-123", spec.Args[^1]);
    }

    [Fact]
    public void NoTempFilesAreCreated()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.EditShell));

        Assert.Empty(spec.TempFiles);
    }

    // Review finding retired 2026-09-21 (issue #14): the prompt moved off argv entirely, onto stdin,
    // so the old single-argument execve limit this backend used to guard against no longer applies.
    [Fact]
    public void AnOversizedPromptBuildsFineNowThatItGoesThroughStdin()
    {
        ResolvedRun run = MakeRun(PermissionLevel.EditShell, brief: new string('x', 200 * 1024));

        ProcessSpec spec = backend.Build(run);

        Assert.Contains(new string('x', 200 * 1024), spec.StdinText, StringComparison.Ordinal);
    }
}
