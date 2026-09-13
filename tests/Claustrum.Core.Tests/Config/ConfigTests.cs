using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
// Alias, not a bare `Config` reference: this file's namespace is nested under `Claustrum.Core`,
// whose enclosing-namespace member lookup finds the *namespace* `Claustrum.Core.Config` before it
// ever considers a using-imported type of the same simple name — the exact trap
// Claustrum.Core.Jobs.JobDirectory.cs already documents and aliases around for itself.
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// Config.Load walks builtin -> user -> repo -> env (docs/PLAN.md §A7); ConfigOverrides (flag) is
// applied later by Resolve. FakePlatform never touches real disk/env, so a fake ".git" marker and
// fake user/repo config paths drive the whole layering deterministically. Paths are built with
// Path.Combine/Path.GetFullPath exactly like GitRootLocator/Config do, rather than hardcoded POSIX
// strings, so the same test passes whether it runs on native Windows or under WSL (dotnet test from
// bash) — Path.GetFullPath("/repo") on Windows resolves against the current drive, not literally.
public sealed class ConfigTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));
    private static readonly string gitMarker = Path.Combine(repoDir, ".git");
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static FakePlatform NewPlatform() => new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };

    private static RenderedRole Builder(string modelClass = "frontier-coding", string permission = "edit+shell") =>
        new("builder", "system body", modelClass, "high", permission, ["git push"], "builder", Blind: false);

    [Fact]
    public void BuiltinModelClassesResolveWithoutAnyConfigFile()
    {
        CoreConfig config = CoreConfig.Load(NewPlatform(), repoDir);

        ResolvedRole resolved = config.Resolve(Builder(), new ConfigOverrides());

        Assert.Equal("claude", resolved.Backend);
        Assert.Equal("opus", resolved.Model);
    }

    [Theory]
    [InlineData("frontier-reasoning", "opus")]
    [InlineData("frontier-coding", "opus")]
    [InlineData("standard-coding", "sonnet")]
    [InlineData("cheap-coding", "haiku")]
    [InlineData("fast", "haiku")]
    public void EveryBuiltinModelClassMapsToClaude(string modelClass, string expectedModel)
    {
        CoreConfig config = CoreConfig.Load(NewPlatform(), repoDir);

        ResolvedRole resolved = config.Resolve(Builder(modelClass), new ConfigOverrides());

        Assert.Equal("claude", resolved.Backend);
        Assert.Equal(expectedModel, resolved.Model);
    }

    [Fact]
    public void RepoLayerOverridesUserLayerWhichOverridesBuiltin()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[userConfigPath] = /*lang=json,strict*/ """{"models":{"frontier-coding":"claude:from-user"}}""";
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"models":{"frontier-coding":"claude:from-repo"}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);

        ResolvedRole resolved = config.Resolve(Builder(), new ConfigOverrides());
        Assert.Equal("from-repo", resolved.Model);
        Assert.Equal(ConfigLayer.Repo, config.Origins["models.frontier-coding"]);
    }

    [Fact]
    public void EnvLayerOverridesRepoLayerForDefaults()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"defaults":{"budget_usd":9}}""";
        platform.ExtraExistingPaths.Add(gitMarker);
        platform.EnvironmentVariables["CLAUSTRUM_BUDGET_USD"] = "42.5";

        CoreConfig config = CoreConfig.Load(platform, repoDir);

        Assert.Equal(42.5m, config.Merged.Defaults?.BudgetUsd);
        Assert.Equal(ConfigLayer.Env, config.Origins["defaults.budget_usd"]);
    }

    [Fact]
    public void FlagOverridesWinOverEveryFileAndEnvLayerAndAreRecordedAsOrigin()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"roles":{"builder":{"model":"claude:from-repo","effort":"low","permission":"readonly"}}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);
        ConfigOverrides overrides = new(Backend: "opencode", Model: "some-model", Effort: "xhigh", Permission: "full", Deny: ["rm -rf"], BudgetUsd: 3m, TimeoutSeconds: 60);

        ResolvedRole resolved = config.Resolve(Builder(), overrides);

        Assert.Equal("opencode", resolved.Backend);
        Assert.Equal("some-model", resolved.Model);
        Assert.Equal("xhigh", resolved.Effort);
        Assert.Equal(PermissionLevel.Full, resolved.Permission.Level);
        Assert.Equal(ConfigLayer.Flag, config.Origins["roles.builder.model"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["roles.builder.effort"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["roles.builder.permission"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["roles.builder.backend"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["roles.builder.deny"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["defaults.budget_usd"]);
        Assert.Equal(ConfigLayer.Flag, config.Origins["defaults.timeout_seconds"]);
    }

    [Fact]
    public void DenyConcatenatesRoleConfigAndFlagLayersInOrder()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"roles":{"builder":{"deny":["git force-push"]}}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);
        ResolvedRole resolved = config.Resolve(Builder(), new ConfigOverrides(Deny: ["rm -rf"]));

        Assert.Equal(["git push", "git force-push", "rm -rf"], resolved.Permission.Deny);
    }

    [Fact]
    public void ModelAliasResolvesThreeHops()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"models":{"a":"b","b":"c","c":"claude:opus"}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);
        ResolvedRole resolved = config.Resolve(Builder("a"), new ConfigOverrides());

        Assert.Equal("claude", resolved.Backend);
        Assert.Equal("opus", resolved.Model);
    }

    [Fact]
    public void ModelAliasFourHopsFails()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"models":{"a":"b","b":"c","c":"d","d":"claude:opus"}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);

        Assert.Throws<ConfigException>(() => config.Resolve(Builder("a"), new ConfigOverrides()));
    }

    [Fact]
    public void ModelAliasCycleFails()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = /*lang=json,strict*/ """{"models":{"a":"b","b":"a"}}""";
        platform.ExtraExistingPaths.Add(gitMarker);

        CoreConfig config = CoreConfig.Load(platform, repoDir);

        Assert.Throws<ConfigException>(() => config.Resolve(Builder("a"), new ConfigOverrides()));
    }

    [Fact]
    public void MalformedJsonThrowsConfigExceptionNamingTheFile()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = "{ not json";
        platform.ExtraExistingPaths.Add(gitMarker);

        ConfigException ex = Assert.Throws<ConfigException>(() => CoreConfig.Load(platform, repoDir));
        Assert.Contains(repoConfigPath, ex.Message);
    }
}
