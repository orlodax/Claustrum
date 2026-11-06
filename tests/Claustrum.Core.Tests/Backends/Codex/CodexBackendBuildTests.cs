using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Codex;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Codex;

// `codex exec --json`, measured 2026-11-04 (NOTES.md "The codex backend, validated against a real install"): the prompt on stdin
// as `-`, the sandbox always as `-c sandbox_mode` because `resume` takes no `-s` and otherwise falls back to workspace-write.
public sealed class CodexBackendBuildTests : IDisposable
{
    private readonly CodexBackend backend = new(platform: null!); // Build never touches IPlatform.
    private readonly string systemPromptPath;

    public CodexBackendBuildTests()
    {
        string directory = Directory.CreateTempSubdirectory("claustrum-codex-build-").FullName;
        systemPromptPath = Path.Combine(directory, "system.md");
        File.WriteAllText(systemPromptPath, "you are a reviewer");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(systemPromptPath)!, recursive: true);

    private ResolvedRun MakeRun(PermissionLevel level, string[]? deny = null, string? resume = null, string effort = "high", string[]? readDirs = null) => new(
        Role: new ResolvedRole("code-reviewer", "you are a reviewer", "codex", "gpt-6.1-sol", effort, new PermissionPolicy(level, deny ?? []), Blind: true, HasReport: true),
        Brief: "review this",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: resume,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: systemPromptPath,
        JobDirectory: "/job",
        Env: [],
        ReadDirs: readDirs);

    [Fact]
    public void ReadOnlyIsTheReadOnlySandboxWithThePromptOnStdinOnly()
    {
        ProcessSpec spec = backend.Build(MakeRun(PermissionLevel.ReadOnly));

        Assert.Equal("codex", spec.Exe);
        Assert.Equal("/repo", spec.Cwd);
        Assert.Equal(
            ["exec", "--json", "--skip-git-repo-check", "--ignore-rules", "--ignore-user-config", "-m", "gpt-6.1-sol", "-c", "sandbox_mode=\"read-only\"", "-c", "model_reasoning_effort=\"high\"", "-"],
            spec.Args);
        Assert.StartsWith("you are a reviewer", spec.StdinText, StringComparison.Ordinal);
        Assert.EndsWith("# Task\nreview this", spec.StdinText, StringComparison.Ordinal);
        Assert.Contains("READ-ONLY", spec.StdinText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PermissionLevel.Edit, "workspace-write", false)]
    [InlineData(PermissionLevel.Shell, "workspace-write", true)]
    [InlineData(PermissionLevel.EditShell, "workspace-write", true)]
    [InlineData(PermissionLevel.Full, "danger-full-access", false)]
    public void EachLevelNamesItsSandboxAndOnlyTheShellLevelsGetTheNetwork(PermissionLevel level, string sandbox, bool network)
    {
        string[] args = backend.Build(MakeRun(level)).Args;

        Assert.Contains($"sandbox_mode=\"{sandbox}\"", args);
        Assert.Equal(network, args.Contains("sandbox_workspace_write.network_access=true"));
    }

    [Fact]
    public void ResumeGoesAfterExecWithTheSandboxStillNamedAndTheIdBeforeTheStdinDash()
    {
        string[] args = backend.Build(MakeRun(PermissionLevel.ReadOnly, resume: "01a1a6bc-f0fd")).Args;

        Assert.Equal(["exec", "resume", "--json", "--skip-git-repo-check", "--ignore-rules", "--ignore-user-config"], args[..6]);
        Assert.Contains("sandbox_mode=\"read-only\"", args);
        Assert.Equal(["01a1a6bc-f0fd", "-"], args[^2..]);
    }

    [Fact]
    public void NoEffortLeavesTheReasoningEffortOut()
    {
        string[] args = backend.Build(MakeRun(PermissionLevel.EditShell, effort: "")).Args;

        Assert.DoesNotContain(args, arg => arg.StartsWith("model_reasoning_effort", StringComparison.Ordinal));
    }

    [Fact]
    public void DenyPatternsAndTheLevelsMissingHalvesAreHardRulesInThePrompt()
    {
        ProcessSpec edit = backend.Build(MakeRun(PermissionLevel.Edit, deny: ["git push"]));
        ProcessSpec shell = backend.Build(MakeRun(PermissionLevel.Shell));
        ProcessSpec editShell = backend.Build(MakeRun(PermissionLevel.EditShell));

        Assert.Contains("- Never run: git push", edit.StdinText, StringComparison.Ordinal);
        Assert.Contains("Never run a shell command", edit.StdinText, StringComparison.Ordinal);
        Assert.Contains("Never edit a file yourself", shell.StdinText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hard rules", editShell.StdinText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadDirsAreReachedWithoutAFlagAndAreNeverWritable()
    {
        string[] args = backend.Build(MakeRun(PermissionLevel.EditShell, readDirs: ["/other"])).Args;

        Assert.True(backend.HonoursReadDirs);
        Assert.DoesNotContain("--add-dir", args);
        Assert.DoesNotContain("/other", args);
        Assert.All(Enum.GetValues<PermissionLevel>(), level => Assert.False(((IBackend)backend).ReadDirsWritableAt(level)));
    }

    [Fact]
    public async Task DetectSaysTheDenyListIsPromptOnlyAsync()
    {
        Doctor doctor = await new CodexBackend(new FakePlatform()).DetectAsync(config: null, TestContext.Current.CancellationToken);

        Assert.Single(doctor.Advisories);
        Assert.Contains("prompt only", doctor.Advisories[0], StringComparison.Ordinal);
    }
}
