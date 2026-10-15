using Claustrum.Core.Config;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Core.Tests;

// #122: every command that needs configuration (run, delegate, coordinate, cast ...) refuses a machine that has neither a
// user config nor a repo claustrum.json, with a message naming both paths. The gate lives in Config.RequireInitialized; the
// commands that call it have their own end-to-end coverage (UninitializedMachineEndToEndTests).
public sealed class ConfigInitializedTests
{
    private static readonly string repoDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-init-repo"));
    private static readonly string homeDir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-init-home"));
    private static readonly string repoConfigPath = Path.Combine(repoDir, "claustrum.json");
    private static readonly string userConfigPath = Path.Combine(homeDir, ".config", "claustrum", "config.json");

    private static FakePlatform NewPlatform() => new() { Os = ClaustrumOs.Linux, HomeDirectory = homeDir };

    private static FakePlatform InRepo(FakePlatform platform)
    {
        platform.ExtraExistingPaths.Add(Path.Combine(repoDir, ".git"));
        return platform;
    }

    [Fact]
    public void AUserConfigAloneIsInitialized()
    {
        FakePlatform platform = InRepo(NewPlatform());
        platform.Files[userConfigPath] = "{}";

        CoreConfig.RequireInitialized(platform, repoDir);
    }

    [Fact]
    public void ARepoClaustrumJsonAloneIsInitialized()
    {
        FakePlatform platform = InRepo(NewPlatform());
        platform.Files[repoConfigPath] = "{}";

        CoreConfig.RequireInitialized(platform, repoDir);
    }

    [Fact]
    public void ARepoClaustrumJsonIsFoundFromASubdirectoryOfTheRepo()
    {
        FakePlatform platform = InRepo(NewPlatform());
        platform.Files[repoConfigPath] = "{}";

        CoreConfig.RequireInitialized(platform, Path.Combine(repoDir, "src", "deep"));
    }

    [Fact]
    public void NeitherFileIsRefusedNamingBothPathsAndTheFix()
    {
        ConfigException error = Assert.Throws<ConfigException>(() => CoreConfig.RequireInitialized(InRepo(NewPlatform()), repoDir));

        Assert.Equal(
            $"this repo isn't initialized: no user config at {userConfigPath} and no claustrum.json at {repoConfigPath}; run `claustrum init`",
            error.Message);
    }

    [Fact]
    public void NoGitRootNamesNoRepoPathAndNeedsTheUserConfig()
    {
        ConfigException error = Assert.Throws<ConfigException>(() => CoreConfig.RequireInitialized(NewPlatform(), repoDir));

        Assert.Equal(
            $"this repo isn't initialized: no user config at {userConfigPath} and no claustrum.json at <no git root>; run `claustrum init`",
            error.Message);
    }

    [Fact]
    public void AClaustrumJsonOutsideAnyGitRootDoesNotCount()
    {
        FakePlatform platform = NewPlatform();
        platform.Files[repoConfigPath] = "{}";

        Assert.Throws<ConfigException>(() => CoreConfig.RequireInitialized(platform, repoDir));
    }

    [Fact]
    public void ANullCwdChecksTheUserConfigAlone()
    {
        Assert.Throws<ConfigException>(() => CoreConfig.RequireInitialized(NewPlatform(), cwd: null));

        FakePlatform platform = NewPlatform();
        platform.Files[userConfigPath] = "{}";
        CoreConfig.RequireInitialized(platform, cwd: null);
    }

    // On Windows the user config is %APPDATA%\claustrum\config.json, not under the home directory.
    [Fact]
    public void OnWindowsTheUserConfigLivesUnderAppData()
    {
        string appData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-appdata"));
        FakePlatform platform = new() { Os = ClaustrumOs.Windows, HomeDirectory = homeDir };
        platform.EnvironmentVariables["APPDATA"] = appData;
        string expected = Path.Combine(appData, "claustrum", "config.json");

        ConfigException error = Assert.Throws<ConfigException>(() => CoreConfig.RequireInitialized(platform, cwd: null));
        Assert.Contains($"no user config at {expected}", error.Message, StringComparison.Ordinal);

        platform.Files[expected] = "{}";
        CoreConfig.RequireInitialized(platform, cwd: null);
    }
}
