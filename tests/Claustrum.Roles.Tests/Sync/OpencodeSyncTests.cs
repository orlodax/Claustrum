using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Mirrors ClaudeSyncTests' coverage (idempotency by construction, foreign-file protection, --force
// adoption, --global, DryRun/Check) for OpencodeSync's own targets: .opencode/agent/<role>.md and
// .opencode/command/claustrum.md. `--global` is exercised via `fakeHome`, a per-test temp directory
// injected as OpencodeSync's `homeDirectory` constructor parameter, so it never touches the real
// `~/.config/opencode`.
public sealed class OpencodeSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-opencode-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-opencode-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private OpencodeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesBaseAgentTierStubsAndCommand()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "builder.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "builder-xhigh.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "builder-max.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("command", "claustrum.md"), StringComparison.Ordinal));
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Foreign);
    }

    // The default "sync everything" case filters to roles that list opencode; the browser roles now do
    // (#135), so they are written like any other role instead of being filtered out.
    [Fact]
    public void DefaultRoleSetIncludesTheBrowserRolesThatListOpencode()
    {
        SyncResult result = NewSync().Sync(cwd);

        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "builder.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "tester.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "ui-reviewer.md"), StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("agent", "demo-author.md"), StringComparison.Ordinal));
    }

    // #135: a browser role's opencode agent grants its Playwright MCP verbs through a `tools:` map; a role
    // with no harness-specific tools gets no `tools:` line at all.
    [Fact]
    public void BrowserAgentFrontmatterGrantsThePlaywrightToolsAndTheBuilderGetsNoToolsLine()
    {
        OpencodeSync sync = NewSync();
        sync.Sync(cwd, roles: ["ui-reviewer"]);
        sync.Sync(cwd, roles: ["builder"]);

        string uiReviewer = File.ReadAllText(Path.Combine(cwd, ".opencode", "agent", "ui-reviewer.md"));
        Assert.Contains("tools:\n  playwright_*: true\n", uiReviewer, StringComparison.Ordinal);

        string builder = File.ReadAllText(Path.Combine(cwd, ".opencode", "agent", "builder.md"));
        Assert.DoesNotMatch(@"(?m)^tools:", builder);
    }

    // #135: the demo-author drives the browser with a scripted driver on opencode, so its agent carries no
    // `tools:` line — an absent opencode override would fall back to the flat `tools` and leak the two
    // Claude-only servers (`mcp__Claude_Browser`, `mcp__claude-in-chrome`) into the mapping.
    [Fact]
    public void DemoAuthorAgentOnOpencodeGetsNoToolsLine()
    {
        NewSync().Sync(cwd, roles: ["demo-author"]);

        string demoAuthor = File.ReadAllText(Path.Combine(cwd, ".opencode", "agent", "demo-author.md"));
        Assert.DoesNotMatch(@"(?m)^tools:", demoAuthor);
    }

    [Fact]
    public void RerunIsFullyIdempotent()
    {
        OpencodeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
    }

    [Fact]
    public void CrlfCheckoutOfAGeneratedFileIsStillSkipped()
    {
        OpencodeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);
        string path = Path.Combine(cwd, ".opencode", "agent", "builder.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));

        SyncResult result = sync.Sync(cwd, roles: ["builder"]);

        Assert.Contains(path, result.Skipped);
        Assert.DoesNotContain(path, result.Written);
    }

    [Fact]
    public void ForeignFileWithoutMarkerIsLeftUntouchedWithoutForce()
    {
        string path = Path.Combine(cwd, ".opencode", "agent", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void ForeignFileIsAdoptedWithForce()
    {
        string path = Path.Combine(cwd, ".opencode", "agent", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(path, result.Written);
        Assert.Contains("claustrum:generated", File.ReadAllText(path));
    }

    [Fact]
    public void GlobalSyncPlacesFilesUnderFakeHomeDotConfig()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        string expectedPath = Path.Combine(fakeHome, ".config", "opencode", "agent", "builder.md");
        Assert.Contains(result.Written, p => p == expectedPath);
        Assert.True(File.Exists(expectedPath));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".opencode")));
    }

    [Fact]
    public void DryRunClassifiesWithoutTouchingDisk()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".opencode")));
        Assert.NotNull(result.ProposedContent);
        Assert.Contains(result.ProposedContent!.Keys, p => p.EndsWith("builder.md", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckAfterARealSyncReportsNothingOutstanding()
    {
        OpencodeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult result = sync.Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Empty(result.Written);
        Assert.Empty(result.Foreign);
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public void AgentFrontmatterCarriesModeSubagentAndAProviderPrefixedModel()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".opencode", "agent", "builder.md"));
        Assert.Contains("mode: subagent", content, StringComparison.Ordinal);
        Assert.Contains("model: openrouter/", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandFrontmatterHasNoNameFieldUnlikeSkillMd()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string content = File.ReadAllText(Path.Combine(cwd, ".opencode", "command", "claustrum.md"));
        Assert.Contains("description:", content, StringComparison.Ordinal);
        Assert.DoesNotContain("name:", content, StringComparison.Ordinal);
    }
}
