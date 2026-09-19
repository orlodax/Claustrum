using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Mirrors ClaudeSyncTests/OpencodeSyncTests' coverage for CopilotSync's own targets:
// .github/agents/<role>.agent.md and .github/skills/claustrum/SKILL.md. `--global` is exercised via
// `fakeHome`, injected as CopilotSync's `homeDirectory` constructor parameter, so it never touches
// the real `~/.copilot`.
public sealed class CopilotSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-copilot-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-copilot-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private CopilotSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesAgentFileTierStubsAndSkill()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder.agent.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder-xhigh.agent.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder-max.agent.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("skills", "claustrum", "SKILL.md"), StringComparison.Ordinal));
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Foreign);
    }

    [Fact]
    public void AgentFilesLandUnderDotGithub()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        Assert.True(File.Exists(Path.Combine(cwd, ".github", "agents", "builder.agent.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md")));
    }

    // Same reasoning as OpencodeSyncTests: ui-reviewer has no environment.default.md fallback.
    [Fact]
    public void DefaultRoleSetExcludesRolesThatDoNotListCopilot()
    {
        SyncResult result = NewSync().Sync(cwd);

        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agents", "builder.agent.md"), StringComparison.Ordinal));
        Assert.DoesNotContain(result.Written, p => p.Contains("ui-reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public void RerunIsFullyIdempotent()
    {
        CopilotSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
    }

    [Fact]
    public void ForeignFileWithoutMarkerIsLeftUntouchedWithoutForce()
    {
        string path = Path.Combine(cwd, ".github", "agents", "builder.agent.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void ForeignFileIsAdoptedWithForce()
    {
        string path = Path.Combine(cwd, ".github", "agents", "builder.agent.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(path, result.Written);
        Assert.Contains("claustrum:generated", File.ReadAllText(path));
    }

    [Fact]
    public void GlobalSyncPlacesFilesUnderFakeHomeDotCopilot()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        string expectedPath = Path.Combine(fakeHome, ".copilot", "agents", "builder.agent.md");
        Assert.Contains(result.Written, p => p == expectedPath);
        Assert.True(File.Exists(expectedPath));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".github")));
    }

    [Fact]
    public void DryRunClassifiesWithoutTouchingDisk()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("builder.agent.md", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".github")));
        Assert.NotNull(result.ProposedContent);
    }

    [Fact]
    public void CheckAfterARealSyncReportsNothingOutstanding()
    {
        CopilotSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult result = sync.Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Empty(result.Written);
        Assert.Empty(result.Foreign);
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public void AgentFrontmatterUsesAutoModelSinceNoTierCatalogWasConfirmed()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".github", "agents", "builder.agent.md"));
        Assert.Contains("model: auto", content, StringComparison.Ordinal);
        Assert.Contains("name: builder", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillDescriptionFrontLoadsTriggerPhrasingSinceThereIsNoSlashCommand()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md"));
        Assert.Contains("name: claustrum", content, StringComparison.Ordinal);
        Assert.Contains("Use when", content, StringComparison.Ordinal);
    }
}
