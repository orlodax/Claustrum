using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

public sealed class OpencodeBackendBuildTests
{
    private readonly OpencodeBackend backend = new(new FakePlatform());

    private static ResolvedRun MakeRun(
        PermissionPolicy permission,
        string effort = "high",
        string? resume = null) => new(
            Role: new ResolvedRole("builder", "system body", "opencode", "openrouter/deepseek/deepseek-v4-pro", effort, permission, Blind: false, HasReport: true),
            Brief: "do the thing",
            Cwd: "/repo",
            BudgetUsd: null,
            ResumeSession: resume,
            AttachFiles: [],
            Stream: false,
            SystemPromptFilePath: "/job/system.md",
            JobDirectory: "/job",
            Env: []);

    [Fact]
    public void BasicArgvShapeMatchesTheOpencodeRunCommand()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        Assert.Equal(
        [
            "run", "--agent", "claustrum-builder", "--model", "openrouter/deepseek/deepseek-v4-pro",
            "--format", "json", "--dir", "/repo",
            "--variant", "high",
            "do the thing",
        ], spec.Args);
        Assert.Equal("opencode", spec.Exe);
        Assert.Empty(spec.TempFiles);
    }

    [Fact]
    public void ConfigContentPointsTheAgentsPromptAtTheSystemPromptFile()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, [])));

        using JsonDocument config = JsonDocument.Parse(spec.Env["OPENCODE_CONFIG_CONTENT"]);
        JsonElement agent = config.RootElement.GetProperty("agent").GetProperty("claustrum-builder");
        Assert.Equal("primary", agent.GetProperty("mode").GetString());
        Assert.Equal("{file:/job/system.md}", agent.GetProperty("prompt").GetString());
    }

    [Fact]
    public void ReadOnlyDeniesEditAndBash()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.ReadOnly, [])));

        using JsonDocument permission = JsonDocument.Parse(spec.Env["OPENCODE_PERMISSION"]);
        Assert.Equal("deny", permission.RootElement.GetProperty("edit").GetString());
        Assert.Equal("deny", permission.RootElement.GetProperty("bash").GetString());
        Assert.DoesNotContain("--auto", spec.Args);
    }

    [Fact]
    public void EditAllowsEditButDeniesBash()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Edit, [])));

        using JsonDocument permission = JsonDocument.Parse(spec.Env["OPENCODE_PERMISSION"]);
        Assert.Equal("allow", permission.RootElement.GetProperty("edit").GetString());
        Assert.Equal("deny", permission.RootElement.GetProperty("bash").GetString());
    }

    [Fact]
    public void EditShellWithDenyAppendsAWildcardBashDenyEntry()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.EditShell, ["git push"])));

        using JsonDocument permission = JsonDocument.Parse(spec.Env["OPENCODE_PERMISSION"]);
        JsonElement bash = permission.RootElement.GetProperty("bash");
        Assert.Equal("allow", bash.GetProperty("*").GetString());
        Assert.Equal("deny", bash.GetProperty("git push*").GetString());
    }

    [Fact]
    public void FullPassesAutoInsteadOfRelyingOnPermissionJsonAlone()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Contains("--auto", spec.Args);
        using JsonDocument permission = JsonDocument.Parse(spec.Env["OPENCODE_PERMISSION"]);
        Assert.Equal("allow", permission.RootElement.GetProperty("edit").GetString());
        Assert.Equal("allow", permission.RootElement.GetProperty("bash").GetString());
    }

    [Fact]
    public void EmptyEffortOmitsVariantFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), effort: ""));

        Assert.DoesNotContain("--variant", spec.Args);
    }

    [Fact]
    public void ResumeSessionIsPassedAsSessionFlag()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, []), resume: "ses-123"));

        int index = Array.IndexOf(spec.Args, "--session");
        Assert.True(index >= 0);
        Assert.Equal("ses-123", spec.Args[index + 1]);
    }

    [Fact]
    public void BriefIsTheFinalArgument()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        Assert.Equal("do the thing", spec.Args[^1]);
    }

    [Fact]
    public void AgentNameIsPrefixedWithClaustrumAndTheRoleName()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Full, [])));

        int index = Array.IndexOf(spec.Args, "--agent");
        Assert.Equal("claustrum-builder", spec.Args[index + 1]);
    }

    [Fact]
    public void ShellDeniesEditButAllowsBashWithTheRolesDenyPatterns()
    {
        ProcessSpec spec = backend.Build(MakeRun(new PermissionPolicy(PermissionLevel.Shell, ["git push"])));

        using JsonDocument permission = JsonDocument.Parse(spec.Env["OPENCODE_PERMISSION"]);
        Assert.Equal("deny", permission.RootElement.GetProperty("edit").GetString());

        JsonElement bash = permission.RootElement.GetProperty("bash");
        Assert.Equal("allow", bash.GetProperty("*").GetString());
        Assert.Equal("deny", bash.GetProperty("git push*").GetString());
        Assert.DoesNotContain("--auto", spec.Args);
    }
}
