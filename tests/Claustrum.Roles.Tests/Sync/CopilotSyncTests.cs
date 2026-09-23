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

    // NOTES.md "The copilot backend, validated against a real install" box 1: an unquoted `": "` in a
    // description is not a valid plain YAML scalar and copilot 1.0.87 silently dropped the file for
    // it. The box measured that on tester, whose description the issue #16 re-port has since rewritten
    // without a `": "`; architect is the role to anchor on now, and a better one — it carries both a
    // `": "` and an inner `"`, so one render exercises both halves below. (ui-reviewer carries both
    // too, but it declares `"harnesses": ["claude"]`, so copilot never renders it.) A strict
    // split on the opening/closing quote is what a hand-rolled YAML-shaped assertion can verify
    // without a real parser.
    [Fact]
    public void DescriptionContainingColonSpaceIsEmittedAsADoubleQuotedYamlScalar()
    {
        NewSync().Sync(cwd, roles: ["architect"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".github", "agents", "architect.agent.md"));
        string descriptionLine = content
            .Split('\n')
            .Single(line => line.StartsWith("description: ", StringComparison.Ordinal));

        Assert.StartsWith("description: \"", descriptionLine, StringComparison.Ordinal);
        Assert.EndsWith("\"", descriptionLine, StringComparison.Ordinal);
        Assert.Contains("It does not ship production code itself: it hands implementation", descriptionLine, StringComparison.Ordinal);

        // The frontmatter is well-formed only if the quote that opens the scalar is the same one
        // that closes it: an inner `"` that were left unescaped would end the scalar early and the
        // next `"` — an entirely different one — would appear to close it correctly by accident.
        string inner = descriptionLine["description: \"".Length..^1];
        Assert.DoesNotContain("\"", inner.Replace("\\\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // A tier stub's description (SyncWriter.TierDescription) also goes through the same
    // BuildAgentFrontmatter path, so it must be quoted too even though none of the built-in tier
    // descriptions happen to contain "": "" today.
    [Fact]
    public void TierStubDescriptionIsAlsoAQuotedYamlScalar()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".github", "agents", "builder-xhigh.agent.md"));
        string descriptionLine = content
            .Split('\n')
            .Single(line => line.StartsWith("description: ", StringComparison.Ordinal));

        Assert.StartsWith("description: \"", descriptionLine, StringComparison.Ordinal);
        Assert.EndsWith("\"", descriptionLine, StringComparison.Ordinal);
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
