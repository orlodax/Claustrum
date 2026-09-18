using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Cursor;

public sealed class CursorBackendBuildTests
{
    private readonly CursorBackend backend = new(new FakePlatform());

    private static ResolvedRun MakeRun(PermissionPolicy permission, string? resume = null) => new(
        Role: new ResolvedRole("code-reviewer", "you are a reviewer", "cursor", "gpt-5", "high", permission, Blind: true, HasReport: true),
        Brief: "review this",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: resume,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: "/job/system.md",
        JobDirectory: "/job",
        Env: []);

    private static ResolvedRun MakeRunWithSystemPrompt(PermissionPolicy permission)
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-cursor-build-").FullName;
        string path = Path.Combine(dir, "system.md");
        File.WriteAllText(path, "you are a reviewer");
        return MakeRun(permission) with { SystemPromptFilePath = path };
    }

    [Fact]
    public void BasicArgvShapeMatchesTheDocumentedForm()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.Equal("cursor-agent", spec.Exe);
        Assert.Equal(["-p", "--output-format", "json", "--model", "gpt-5"], spec.Args[..5]);
        Assert.Contains("--workspace", spec.Args);
        Assert.Equal("/repo", spec.Args[Array.IndexOf(spec.Args, "--workspace") + 1]);
    }

    [Fact]
    public void SystemPromptAndBriefAreComposedIntoTheFinalArgument()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, [])));

        string prompt = spec.Args[^1];
        Assert.StartsWith("you are a reviewer", prompt, StringComparison.Ordinal);
        Assert.Contains("# Task\nreview this", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void DenyPatternsAreAppendedToThePromptAsHardRules()
    {
        ResolvedRun run = MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, ["git push", "rm -rf"]));

        ProcessSpec spec = backend.Build(run);

        string prompt = spec.Args[^1];
        Assert.Contains("Never run: git push", prompt, StringComparison.Ordinal);
        Assert.Contains("Never run: rm -rf", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDenyPatternsOmitsTheHardRulesSection()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.DoesNotContain("Hard rules", spec.Args[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyUsesAskModeWithoutForceFlag()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        Assert.Contains("--mode", spec.Args);
        Assert.Equal("ask", spec.Args[Array.IndexOf(spec.Args, "--mode") + 1]);
        Assert.DoesNotContain("-f", spec.Args);
    }

    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void EditAndEditShellPassForceFlag(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(level, [])));

        Assert.Contains("-f", spec.Args);
    }

    [Fact]
    public void FullPassesForceAndDisabledSandbox()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Contains("-f", spec.Args);
        Assert.Contains("--sandbox", spec.Args);
        Assert.Equal("disabled", spec.Args[Array.IndexOf(spec.Args, "--sandbox") + 1]);
    }

    [Fact]
    public void ResumeSessionIsPassedAsResumeFlag()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.Full, [])) with { ResumeSession = "sess-123" });

        int index = Array.IndexOf(spec.Args, "--resume");
        Assert.True(index >= 0);
        Assert.Equal("sess-123", spec.Args[index + 1]);
    }

    [Fact]
    public void NoTempFilesAreCreated()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.Empty(spec.TempFiles);
    }
}
