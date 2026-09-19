using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Copilot;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Copilot;

public sealed class CopilotBackendBuildTests : IDisposable
{
    private readonly string jobDirectory = Directory.CreateTempSubdirectory("claustrum-copilot-build-").FullName;
    private readonly CopilotBackend backend = new(new FakePlatform());

    public void Dispose() => Directory.Delete(jobDirectory, recursive: true);

    private ResolvedRun MakeRun(
        PermissionPolicy permission,
        string effort = "high",
        string? resume = null) => new(
            Role: new ResolvedRole("builder", "system body", "copilot", "gpt-5", effort, permission, Blind: false, HasReport: true),
            Brief: "do the thing",
            Cwd: "/repo",
            BudgetUsd: null,
            ResumeSession: resume,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: WriteSystemPrompt(),
            JobDirectory: jobDirectory,
            Env: []);

    private string WriteSystemPrompt()
    {
        string path = Path.Combine(jobDirectory, "system.md");
        File.WriteAllText(path, "you are a builder");
        return path;
    }

    [Fact]
    public void WritesAnAgentFileUnderAddDirsGithubAgentsDirectory()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        string agentFile = Assert.Single(spec.TempFiles);
        Assert.EndsWith(Path.Combine(".github", "agents", "claustrum-builder.agent.md"), agentFile, StringComparison.Ordinal);
        Assert.True(File.Exists(agentFile));
        string content = File.ReadAllText(agentFile);
        Assert.Contains("name: claustrum-builder", content, StringComparison.Ordinal);
        Assert.Contains("you are a builder", content, StringComparison.Ordinal);

        int addDirIndex = Array.IndexOf(spec.Args, "--add-dir");
        Assert.True(addDirIndex >= 0);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(agentFile))), spec.Args[addDirIndex + 1]);
    }

    [Fact]
    public void BasicArgvIncludesCwdAgentAndJsonOutput()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.Equal("copilot", spec.Exe);
        Assert.Equal(["-C", "/repo"], spec.Args[..2]);
        Assert.Contains("--agent", spec.Args);
        Assert.Contains("claustrum-builder", spec.Args);
        int formatIndex = Array.IndexOf(spec.Args, "--output-format");
        Assert.Equal("json", spec.Args[formatIndex + 1]);
        Assert.Equal(["-p", "do the thing"], spec.Args[^2..]);
    }

    [Fact]
    public void ReadOnlyAllowsToolsButDeniesWriteAndShellAndUsesPlanMode()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        Assert.Contains("--allow-all-tools", spec.Args);
        Assert.Contains("plan", spec.Args);
        Assert.Contains("write", spec.Args);
        Assert.Contains("shell", spec.Args);
        Assert.Equal(2, spec.Args.Count(a => a == "--deny-tool"));
    }

    [Fact]
    public void EditAllowsPathsAndToolsButDeniesShell()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Edit, [])));

        Assert.Contains("--allow-all-tools", spec.Args);
        Assert.Contains("--allow-all-paths", spec.Args);
        int denyIndex = Array.IndexOf(spec.Args, "--deny-tool");
        Assert.Equal("shell", spec.Args[denyIndex + 1]);
    }

    [Fact]
    public void EditShellWithDenyPatternsDeniesEachAsAShellTool()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, ["git push", "rm -rf"])));

        Assert.Contains("--allow-all-tools", spec.Args);
        Assert.Contains("shell(git push)", spec.Args);
        Assert.Contains("shell(rm -rf)", spec.Args);
    }

    [Fact]
    public void EditShellWithoutDenyOmitsDenyTool()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.DoesNotContain("--deny-tool", spec.Args);
    }

    [Fact]
    public void FullUsesTheSingleAllowAllFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Contains("--allow-all", spec.Args);
        Assert.DoesNotContain("--allow-all-tools", spec.Args);
    }

    [Fact]
    public void EffortIsPassedAsReasoningEffort()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: "xhigh"));

        int index = Array.IndexOf(spec.Args, "--reasoning-effort");
        Assert.True(index >= 0);
        Assert.Equal("xhigh", spec.Args[index + 1]);
    }

    [Fact]
    public void EmptyEffortOmitsReasoningEffortFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: ""));

        Assert.DoesNotContain("--reasoning-effort", spec.Args);
    }

    [Fact]
    public void ResumeIsPassedAsResumeFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), resume: "sess-123"));

        int index = Array.IndexOf(spec.Args, "--resume");
        Assert.True(index >= 0);
        Assert.Equal("sess-123", spec.Args[index + 1]);
    }

    [Fact]
    public void PromptIsTheFinalTwoArguments()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Equal("-p", spec.Args[^2]);
        Assert.Equal("do the thing", spec.Args[^1]);
    }

    [Fact]
    public void ShellAllowsToolsButDeniesWriteOnly()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Shell, [])));

        Assert.Contains("--allow-all-tools", spec.Args);
        Assert.Contains("write", spec.Args);
        Assert.DoesNotContain("--allow-all-paths", spec.Args);
        Assert.Equal(1, spec.Args.Count(a => a == "--deny-tool"));
    }
}
