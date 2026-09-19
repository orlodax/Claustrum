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

    // Deliberate divergence from docs/PLAN.md §A3's cursor column ("--mode ask (no -f)"): `-p` is
    // headless and ProcessRunner closes the child's stdin, so an approval prompt nobody can answer
    // stalls the run until --timeout kills it — and readonly is the level the most likely cursor
    // role (code-reviewer) uses (review finding). cursor has no native deny mechanism at any level,
    // so the read-only rule goes where cursor's deny list already goes: the prompt.
    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    public void ReadOnlyAndShellForceHeadlessApprovalRatherThanAskMode(PermissionLevel level)
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(level, [])));

        Assert.Contains("-f", spec.Args);
        Assert.DoesNotContain("--mode", spec.Args);
    }

    [Fact]
    public void ReadOnlyStatesTheNoEditRuleInThePromptSinceCursorHasNoFlagForIt()
    {
        ProcessSpec spec = backend.Build(MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        string prompt = spec.Args[^1];
        Assert.Contains("READ-ONLY", prompt, StringComparison.Ordinal);
        Assert.Contains("Hard rules", prompt, StringComparison.Ordinal);
    }

    // Review finding: the whole system prompt + brief go in ONE argv element, and execve caps a
    // single argument at 128KB on Linux. Over the limit this used to be a bare spawn failure.
    [Fact]
    public void AnOversizedPromptIsRefusedByNameInsteadOfFailingInExecve()
    {
        ResolvedRun run = MakeRunWithSystemPrompt(new PermissionPolicy(PermissionLevel.EditShell, [])) with
        {
            Brief = new string('x', 200 * 1024),
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => backend.Build(run));

        Assert.Contains("single-argument limit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("shorten the brief", ex.Message, StringComparison.Ordinal);
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
