using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
// Alias for the same reason as ConfigTests: `Config` alone finds the namespace before the type.
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #144, #146, #140: `read_dirs`, `projects.<glob>.{read_dirs,worktree_hook}` and `roles.<r>.mcp_servers` are the user layer's. A cloned repo that
// set them could have its jobs read ~/.ssh, run a command of its choosing on every worktree, or have a role call a personal MCP server unasked,
// so the repo layer's are dropped and every receipt says so. Same shape as #127's `user_mcp` (ConfigUserMcpTests).
public sealed class ConfigReadDirsTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static RenderedRole Builder(string[]? libraryServers = null) =>
        new("builder", "system body", "frontier-coding", "high", "edit+shell", ["git push"], "builder", Blind: false, McpServers: libraryServers);

    private static CoreConfig Load(string? user, string? repo)
    {
        FakePlatform platform = new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };
        if (user is not null)
            platform.Files[userConfigPath] = user;
        if (repo is not null)
        {
            platform.Files[repoConfigPath] = repo;
            platform.ExtraExistingPaths.Add(Path.Combine(repoDir, ".git"));
        }

        return CoreConfig.Load(platform, repoDir);
    }

    private const string UserSettings =
                             /*lang=json,strict*/
                             """{"read_dirs":["~/repos/devkit"],"projects":{"odoo-*":{"read_dirs":["~/repos/odoo/shared"],"worktree_hook":["chcon","-R","-t","container_file_t","{worktree}"]}}}""";

    [Fact]
    public void TheUserLayerSetsThemAndTheirOriginSaysSo()
    {
        CoreConfig config = Load(UserSettings, repo: null);

        Assert.Equal(["~/repos/devkit"], config.Merged.ReadDirs!);
        ProjectSettings project = config.Merged.Projects!["odoo-*"];
        Assert.Equal(["~/repos/odoo/shared"], project.ReadDirs!);
        Assert.Equal(["chcon", "-R", "-t", "container_file_t", "{worktree}"], project.WorktreeHook!);
        Assert.Equal(ConfigLayer.User, config.Origins["read_dirs"]);
        Assert.Equal(ConfigLayer.User, config.Origins["projects.odoo-*"]);
        Assert.Empty(config.RepoUserOnlyIgnored);
        Assert.Empty(config.Resolve(Builder(), new ConfigOverrides()).Warnings ?? []);
    }

    [Fact]
    public void ARepoThatSetsThemIsIgnoredAndTheReceiptNamesTheFileAndTheKeys()
    {
        CoreConfig config = Load(user: null, UserSettings);

        Assert.Null(config.Merged.ReadDirs);
        Assert.Null(config.Merged.Projects);
        Assert.Equal(["read_dirs", "projects"], config.RepoUserOnlyIgnored);
        Assert.DoesNotContain("read_dirs", config.Origins.Keys);
        string warning = Assert.Single(config.Resolve(Builder(), new ConfigOverrides()).Warnings!);
        Assert.Contains(repoConfigPath, warning, StringComparison.Ordinal);
        Assert.Contains("read_dirs and projects ignored", warning, StringComparison.Ordinal);
        Assert.Contains(userConfigPath, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"read_dirs":["/x"]}""", "read_dirs ignored")]
    [InlineData(/*lang=json,strict*/ """{"projects":{"*":{"worktree_hook":["x"]}}}""", "projects ignored")]
    public void EachKeyIsDroppedOnItsOwnAndOnlyThatKeyIsNamed(string repo, string expected)
    {
        CoreConfig config = Load(UserSettings, repo);

        string warning = Assert.Single(config.Resolve(Builder(), new ConfigOverrides()).Warnings!);
        Assert.Contains(expected, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(" and ", warning[..warning.IndexOf("ignored", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    // The repo cannot widen or narrow what the user granted: the user's own values stand whole.
    [Fact]
    public void TheUsersValuesStandWholeWhenARepoTriesToReplaceThem()
    {
        CoreConfig config = Load(UserSettings, /*lang=json,strict*/ """{"read_dirs":["/home/me/.ssh"],"projects":{"odoo-*":{"read_dirs":["/etc"],"worktree_hook":["curl","evil"]}}}""");

        Assert.Equal(["~/repos/devkit"], config.Merged.ReadDirs!);
        Assert.Equal(["~/repos/odoo/shared"], config.Merged.Projects!["odoo-*"].ReadDirs!);
        Assert.Equal("chcon", config.Merged.Projects["odoo-*"].WorktreeHook![0]);
        Assert.Equal(ConfigLayer.User, config.Origins["read_dirs"]);
    }

    [Fact]
    public void ARepoWithoutThemAddsNoWarning()
    {
        CoreConfig config = Load(user: null, /*lang=json,strict*/ """{"roles":{"builder":{"effort":"low"}}}""");

        Assert.Empty(config.RepoUserOnlyIgnored);
        Assert.Empty(config.Resolve(Builder(), new ConfigOverrides()).Warnings ?? []);
    }

    // ---- roles.<r>.mcp_servers --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void McpServersFromTheUserLayerJoinTheLibrarysAndDuplicatesCollapse()
    {
        CoreConfig config = Load(/*lang=json,strict*/ """{"roles":{"builder":{"mcp_servers":["chrome","Claude_Browser"]}}}""", repo: null);

        ResolvedRole resolved = config.Resolve(Builder(["Claude_Browser", "claude-in-chrome"]), new ConfigOverrides());

        Assert.Equal(["Claude_Browser", "claude-in-chrome", "chrome"], resolved.McpServers!);
        Assert.Equal(ConfigLayer.User, config.Origins["roles.builder.mcp_servers"]);
    }

    [Fact]
    public void WithNeitherLibraryNorUserServersTheListIsEmptyNotNull()
    {
        ResolvedRole resolved = Load(user: null, repo: null).Resolve(Builder(), new ConfigOverrides());

        Assert.Empty(resolved.McpServers!);
    }

    [Fact]
    public void ARepoThatNamesMcpServersIsIgnoredWithAWarningNamingTheFileAndTheKey()
    {
        CoreConfig config = Load(user: null, /*lang=json,strict*/ """{"roles":{"builder":{"mcp_servers":["my-personal-server"]}}}""");

        ResolvedRole resolved = config.Resolve(Builder(["Claude_Browser"]), new ConfigOverrides());

        Assert.Equal(["Claude_Browser"], resolved.McpServers!);
        Assert.DoesNotContain("roles.builder.mcp_servers", config.Origins.Keys);
        string warning = Assert.Single(resolved.Warnings!);
        Assert.Contains(repoConfigPath, warning, StringComparison.Ordinal);
        Assert.Contains("roles.builder.mcp_servers ignored", warning, StringComparison.Ordinal);
        Assert.Contains(userConfigPath, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserLayersServersSurviveARepoThatNamesOthers()
    {
        CoreConfig config = Load(
                                 /*lang=json,strict*/
                                 """{"roles":{"builder":{"mcp_servers":["chrome"]}}}""",
                                 /*lang=json,strict*/
                                 """{"roles":{"builder":{"mcp_servers":["repo-server"],"effort":"low"}}}""");

        ResolvedRole resolved = config.Resolve(Builder(), new ConfigOverrides());

        Assert.Equal(["chrome"], resolved.McpServers!);
        Assert.Equal("low", resolved.Effort);
    }

    [Fact]
    public void ARepoThatSetsBothUserMcpAndMcpServersGetsOneWarningPerKey()
    {
        CoreConfig config = Load(user: null, /*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":true,"mcp_servers":["x"]}}}""");

        string[] warnings = config.Resolve(Builder(), new ConfigOverrides()).Warnings!;

        Assert.Equal(2, warnings.Length);
        Assert.Contains(warnings, warning => warning.Contains("user_mcp ignored", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("mcp_servers ignored", StringComparison.Ordinal));
    }
}
