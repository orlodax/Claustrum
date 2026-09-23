using System.Text.Json.Nodes;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// docs/PLAN.md §B4: idempotency by construction (marker + manifest), foreign-file protection,
// `--force` adoption. `--global` is exercised via `fakeHome`, a per-test temp directory injected as
// ClaudeSync's `homeDirectory` constructor parameter, so it never touches the real `~/.claude`.
public sealed class ClaudeSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private ClaudeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesBaseAgentTierStubsAndSkill()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith("builder-xhigh.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith("builder-max.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("skills", "claustrum", "SKILL.md"), StringComparison.Ordinal));
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Foreign);
    }

    // #29: `shell` used to fall through to the edit tool set, so a browser role was synced with
    // Edit/Write its own ground rules forbid and no browser tool at all. Driven off ListRoles so a
    // shell role added later is covered instead of silently skipped.
    [Fact]
    public void NoShellRoleIsSyncedWithEditAndEveryToolItAsksForIsGrantedNotDenied()
    {
        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;
            if (definition.Permission != "shell")
                continue;

            NewSync().Sync(cwd, roles: [role]);
            string file = File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{role}.md"));
            string tools = FrontmatterValue(file, "tools");
            string disallowed = FrontmatterValue(file, "disallowedTools");

            Assert.DoesNotContain("Edit", tools, StringComparison.Ordinal);
            Assert.Contains("Edit", disallowed, StringComparison.Ordinal);
            Assert.Contains("NotebookEdit", disallowed, StringComparison.Ordinal);

            // Granting and denying the same tool is incoherent, so an extra drops out of the deny list.
            foreach (string extra in definition.ExtraTools)
            {
                Assert.Contains(extra, tools, StringComparison.Ordinal);
                Assert.DoesNotContain(extra, disallowed, StringComparison.Ordinal);
            }
        }
    }

    // The two browser-bound roles are the reason the rung exists: a ui-reviewer or demo-author that
    // cannot open a browser cannot do the one thing it is for.
    [Theory]
    [InlineData("ui-reviewer")]
    [InlineData("demo-author")]
    public void ABrowserRoleIsSyncedWithTheBrowserTools(string role)
    {
        NewSync().Sync(cwd, roles: [role]);

        string tools = FrontmatterValue(File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{role}.md")), "tools");

        Assert.Contains("mcp__Claude_Browser", tools, StringComparison.Ordinal);
        Assert.Contains("mcp__claude-in-chrome", tools, StringComparison.Ordinal);
    }

    private static string FrontmatterValue(string file, string key)
    {
        string prefix = $"{key}: ";
        string? line = file.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..];
    }

    [Fact]
    public void RerunIsFullyIdempotent()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
    }

    [Fact]
    public void CrlfCheckoutOfAGeneratedFileIsStillSkipped()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));

        SyncResult result = sync.Sync(cwd, roles: ["builder"]);

        Assert.Contains(path, result.Skipped);
        Assert.DoesNotContain(path, result.Written);
    }

    [Fact]
    public void ForeignFileWithoutMarkerIsLeftUntouchedWithoutForce()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void ForeignFileIsAdoptedWithForce()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(path, result.Written);
        Assert.Contains("claustrum:generated", File.ReadAllText(path));
    }

    [Fact]
    public void SyncWritesTheManifestWithEveryGeneratedFile()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string manifestPath = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("builder.md", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void GlobalSyncPlacesFilesUnderAFakeHome()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        string expectedPath = Path.Combine(fakeHome, ".claude", "agents", "builder.md");
        Assert.Contains(result.Written, p => p == expectedPath);
        Assert.True(File.Exists(expectedPath));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public void DryRunClassifiesWithoutTouchingDisk()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
        Assert.NotNull(result.ProposedContent);
        Assert.Contains(result.ProposedContent!.Keys, p => p.EndsWith("builder.md", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckOnAFreshRepoReportsEverythingMissingWithoutWriting()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.NotEmpty(result.Written);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public void CheckAfterARealSyncReportsNothingOutstanding()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult result = sync.Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Empty(result.Written);
        Assert.Empty(result.Foreign);
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public void CheckStillDetectsAForeignFileWithoutAdoptingIt()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteModeNeverPopulatesProposedContent()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Null(result.ProposedContent);
    }

    // issue #24: the fourth --global target, the Claude desktop app's own config — merged only under
    // global: true and only when the caller has one to hand in (desktop is not null); the command is
    // the caller-resolved absolute binary path, never the bare "claustrum" the repo-level .mcp.json
    // entries use, because the desktop app spawns the server from a GUI process whose PATH rarely
    // holds the install directory.
    [Fact]
    public void GlobalSyncWithADesktopTargetWritesOnlyTheClaustrumKeyWithTheAbsolutePath()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"));

        Assert.Contains(desktopConfigPath, result.Written);
        JsonObject root = JsonNode.Parse(File.ReadAllText(desktopConfigPath))!.AsObject();
        JsonObject servers = root["mcpServers"]!.AsObject();
        Assert.Single(servers);
        Assert.Equal("/abs/claustrum", servers["claustrum"]!["command"]!.GetValue<string>());
        Assert.Equal("mcp", servers["claustrum"]!["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void GlobalSyncWithADesktopTargetIsIdempotentOnRerun()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");
        ClaudeSync sync = NewSync();
        ClaudeDesktopTarget desktop = new(desktopConfigPath, "/abs/claustrum");
        sync.Sync(cwd, roles: ["builder"], global: true, desktop: desktop);

        SyncResult second = sync.Sync(cwd, roles: ["builder"], global: true, desktop: desktop);

        Assert.Contains(desktopConfigPath, second.Skipped);
        Assert.DoesNotContain(desktopConfigPath, second.Written);
    }

    [Fact]
    public void GlobalSyncWithADesktopTargetPreservesAForeignSiblingServer()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");
        File.WriteAllText(desktopConfigPath, /*lang=json,strict*/
            """{"mcpServers":{"other-server":{"command":"other","args":[]}}}""");

        NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"));

        JsonObject servers = JsonNode.Parse(File.ReadAllText(desktopConfigPath))!.AsObject()["mcpServers"]!.AsObject();
        Assert.Equal(2, servers.Count);
        Assert.Equal("other", servers["other-server"]!["command"]!.GetValue<string>());
        Assert.Equal("/abs/claustrum", servers["claustrum"]!["command"]!.GetValue<string>());
    }

    // McpProvenance.OwnKey: no repo, no manifest to consult for this target, so a differing
    // `claustrum` entry (a stale path from a previous install, or a hand-written one — the exact gap
    // INSTALL.md used to tell desktop users to close by hand) is rewritten without --force.
    [Fact]
    public void GlobalSyncWithADesktopTargetRewritesADifferingClaustrumEntryWithoutForce()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");
        File.WriteAllText(desktopConfigPath, /*lang=json,strict*/
            """{"mcpServers":{"claustrum":{"command":"/old/stale/claustrum","args":["mcp"]}}}""");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"), force: false);

        Assert.Contains(desktopConfigPath, result.Written);
        Assert.DoesNotContain(desktopConfigPath, result.Foreign);
        JsonObject servers = JsonNode.Parse(File.ReadAllText(desktopConfigPath))!.AsObject()["mcpServers"]!.AsObject();
        Assert.Equal("/abs/claustrum", servers["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void GlobalSyncWithADesktopTargetUnderCheckWritesNothing()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"), mode: SyncMode.Check);

        Assert.Contains(desktopConfigPath, result.Written);
        Assert.False(File.Exists(desktopConfigPath));
    }

    [Fact]
    public void GlobalSyncWithADesktopTargetUnderDryRunWritesNothing()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"), mode: SyncMode.DryRun);

        Assert.Contains(desktopConfigPath, result.Written);
        Assert.NotNull(result.ProposedContent);
        Assert.Contains(desktopConfigPath, result.ProposedContent!.Keys);
        Assert.False(File.Exists(desktopConfigPath));
    }

    [Fact]
    public void NonGlobalSyncNeverTouchesTheDesktopConfigEvenWhenOneIsGiven()
    {
        string desktopConfigPath = Path.Combine(fakeHome, "claude_desktop_config.json");

        NewSync().Sync(cwd, roles: ["builder"], global: false, desktop: new ClaudeDesktopTarget(desktopConfigPath, "/abs/claustrum"));

        Assert.False(File.Exists(desktopConfigPath));
    }

    [Fact]
    public void GlobalSyncWithNoDesktopTargetNeverCreatesTheDesktopConfig()
    {
        NewSync().Sync(cwd, roles: ["builder"], global: true, desktop: null);

        Assert.False(File.Exists(Path.Combine(fakeHome, "claude_desktop_config.json")));
    }
}
