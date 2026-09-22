using System.Text.Json.Nodes;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Mirrors ClaudeSyncTests/CopilotSyncTests/OpencodeSyncTests' coverage for CursorSync's own targets:
// .cursor/agents/<role>.md (+ tier stubs), .cursor/skills/claustrum/SKILL.md and .cursor/mcp.json
// (issue #25). `--global` is exercised via `fakeHome`, injected as CursorSync's `homeDirectory`
// constructor parameter, so it never touches the real `~/.cursor`.
public sealed class CursorSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cursor-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-cursor-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private CursorSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesBaseAgentTierStubsSkillAndMcpJson()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder-xhigh.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder-max.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("skills", "claustrum", "SKILL.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith("mcp.json", StringComparison.Ordinal));
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Foreign);
    }

    [Fact]
    public void AgentFilesLandUnderDotCursor()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "skills", "claustrum", "SKILL.md")));
    }

    // Every default role (architect, builder, code-reviewer, tester) times its base file + two tier
    // stubs (xhigh, max) = 12 agent files; ui-reviewer is the only library role that does not list
    // "cursor" in its harnesses, so an unfiltered sync excludes it entirely.
    [Fact]
    public void DefaultRoleSetWritesTwelveAgentFilesAndExcludesUiReviewer()
    {
        SyncResult result = NewSync().Sync(cwd);

        string[] agentFiles = [.. result.Written.Where(p => p.Contains(Path.Combine(".cursor", "agents"), StringComparison.Ordinal))];
        Assert.Equal(12, agentFiles.Length);
        Assert.DoesNotContain(agentFiles, p => p.Contains("ui-reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public void RerunIsFullyIdempotent()
    {
        CursorSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
    }

    [Fact]
    public void ForeignFileWithoutMarkerIsLeftUntouchedWithoutForce()
    {
        string path = Path.Combine(cwd, ".cursor", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void ForeignFileIsAdoptedWithForce()
    {
        string path = Path.Combine(cwd, ".cursor", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(path, result.Written);
        Assert.Contains("claustrum:generated", File.ReadAllText(path));
    }

    [Fact]
    public void RepoMcpJsonRegistersTheBareCommand()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        JsonObject servers = JsonNode.Parse(File.ReadAllText(Path.Combine(cwd, ".cursor", "mcp.json")))!.AsObject()["mcpServers"]!.AsObject();
        Assert.Equal("claustrum", servers["claustrum"]!["command"]!.GetValue<string>());
        Assert.Equal("mcp", servers["claustrum"]!["args"]![0]!.GetValue<string>());
    }

    // .cursor/mcp.json is committed and shared; a machine-specific absolute path in it would be
    // wrong for everyone else, so unlike the global merge it always gets the bare name regardless of
    // globalBinaryPath — which is meaningless here anyway since global defaults to false.
    [Fact]
    public void RepoMcpJsonForeignSiblingIsPreservedAndManifestTracked()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".cursor"));
        File.WriteAllText(Path.Combine(cwd, ".cursor", "mcp.json"), /*lang=json,strict*/
            """{"mcpServers":{"other-server":{"command":"other","args":[]}}}""");

        NewSync().Sync(cwd, roles: ["builder"]);

        JsonObject servers = JsonNode.Parse(File.ReadAllText(Path.Combine(cwd, ".cursor", "mcp.json")))!.AsObject()["mcpServers"]!.AsObject();
        Assert.Equal(2, servers.Count);
        Assert.Equal("other", servers["other-server"]!["command"]!.GetValue<string>());
        Assert.Equal("claustrum", servers["claustrum"]!["command"]!.GetValue<string>());

        string manifestPath = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("mcp.json", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void GlobalSyncPlacesAgentsAndSkillsUnderFakeHomeDotCursor()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        string expectedAgent = Path.Combine(fakeHome, ".cursor", "agents", "builder.md");
        string expectedSkill = Path.Combine(fakeHome, ".cursor", "skills", "claustrum", "SKILL.md");
        Assert.Contains(result.Written, p => p == expectedAgent);
        Assert.Contains(result.Written, p => p == expectedSkill);
        Assert.True(File.Exists(expectedAgent));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".cursor")));
    }

    // globalBinaryPath is null by default: registering the dotnet host under the `claustrum` key
    // would be worse than leaving the file alone, so the global mcp.json merge is skipped entirely.
    [Fact]
    public void GlobalSyncWithNoBinaryPathSkipsTheGlobalMcpJsonEntirely()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, globalBinaryPath: null);

        Assert.DoesNotContain(result.Written, p => p.EndsWith("mcp.json", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fakeHome, ".cursor", "mcp.json")));
    }

    [Fact]
    public void GlobalSyncWithABinaryPathWritesTheAbsolutePathAsTheCommand()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, globalBinaryPath: "/abs/claustrum");

        string mcpJsonPath = Path.Combine(fakeHome, ".cursor", "mcp.json");
        Assert.Contains(result.Written, p => p == mcpJsonPath);
        JsonObject servers = JsonNode.Parse(File.ReadAllText(mcpJsonPath))!.AsObject()["mcpServers"]!.AsObject();
        Assert.Equal("/abs/claustrum", servers["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void ReadonlyTrueIsPresentForCodeReviewerAndAbsentForBuilder()
    {
        NewSync().Sync(cwd, roles: ["builder", "code-reviewer"]);

        string builderContent = File.ReadAllText(Path.Combine(cwd, ".cursor", "agents", "builder.md"));
        string reviewerContent = File.ReadAllText(Path.Combine(cwd, ".cursor", "agents", "code-reviewer.md"));
        Assert.DoesNotContain("readonly:", builderContent, StringComparison.Ordinal);
        Assert.Contains("readonly: true", reviewerContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentFrontmatterUsesModelInheritForEveryRoleAndTier()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains("model: inherit", File.ReadAllText(Path.Combine(cwd, ".cursor", "agents", "builder.md")), StringComparison.Ordinal);
        Assert.Contains("model: inherit", File.ReadAllText(Path.Combine(cwd, ".cursor", "agents", "builder-xhigh.md")), StringComparison.Ordinal);
        Assert.Contains("model: inherit", File.ReadAllText(Path.Combine(cwd, ".cursor", "agents", "builder-max.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void SkillFrontmatterNamesClaustrumAndOmitsDisableModelInvocation()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".cursor", "skills", "claustrum", "SKILL.md"));
        Assert.Contains("name: claustrum", content, StringComparison.Ordinal);
        Assert.DoesNotContain("disable-model-invocation", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunClassifiesWithoutTouchingDisk()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".cursor")));
        Assert.NotNull(result.ProposedContent);
    }

    [Fact]
    public void CheckAfterARealSyncReportsNothingOutstanding()
    {
        CursorSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult result = sync.Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Empty(result.Written);
        Assert.Empty(result.Foreign);
        Assert.NotEmpty(result.Skipped);
    }
}
