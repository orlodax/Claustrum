using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
// Alias for the same reason as ConfigTests: `Config` alone finds the namespace before the type.
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #128: roles.<role>.user_mcp merges across the user and repo layers like the other role settings, records which layer set it,
// and reaches the ResolvedRole the claude backend reads. Unset means the strict default: false.
public sealed class ConfigUserMcpTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static RenderedRole Builder() => new("builder", "system body", "frontier-coding", "high", "edit+shell", ["git push"], "builder", Blind: false);

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

    [Fact]
    public void UnsetMeansFalseAndLeavesNoOrigin()
    {
        CoreConfig config = Load(user: null, repo: null);

        Assert.False(config.Resolve(Builder(), new ConfigOverrides()).UserMcp);
        Assert.DoesNotContain("roles.builder.user_mcp", config.Origins.Keys);
    }

    [Fact]
    public void TheUserLayerCanOptARoleInAndTheOriginSaysSo()
    {
        CoreConfig config = Load(/*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":true}}}""", repo: null);

        Assert.True(config.Resolve(Builder(), new ConfigOverrides()).UserMcp);
        Assert.Equal(ConfigLayer.User, config.Origins["roles.builder.user_mcp"]);
    }

    [Fact]
    public void TheRepoLayerOverridesTheUserLayerAndTakesTheOrigin()
    {
        CoreConfig config = Load(
            /*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":true}}}""",
            /*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":false}}}""");

        Assert.False(config.Resolve(Builder(), new ConfigOverrides()).UserMcp);
        Assert.Equal(ConfigLayer.Repo, config.Origins["roles.builder.user_mcp"]);
    }

    // A later layer that does not mention the key must not reset it, and must not steal its origin.
    [Fact]
    public void ALaterLayerThatSaysNothingAboutItKeepsTheEarlierValueAndOrigin()
    {
        CoreConfig config = Load(
            /*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":true}}}""",
            /*lang=json,strict*/ """{"roles":{"builder":{"model":"claude:from-repo"}}}""");

        ResolvedRole resolved = config.Resolve(Builder(), new ConfigOverrides());

        Assert.True(resolved.UserMcp);
        Assert.Equal(ConfigLayer.User, config.Origins["roles.builder.user_mcp"]);
        Assert.Equal(ConfigLayer.Repo, config.Origins["roles.builder.model"]);
    }

    [Fact]
    public void TheSettingIsPerRole()
    {
        CoreConfig config = Load(/*lang=json,strict*/ """{"roles":{"builder":{"user_mcp":true}}}""", repo: null);
        RenderedRole reviewer = new("code-reviewer", "system body", "frontier-coding", "high", "readonly", [], "code-reviewer", Blind: true);

        Assert.False(config.Resolve(reviewer, new ConfigOverrides()).UserMcp);
    }
}
