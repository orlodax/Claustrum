using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Process;

public sealed class EnvAllowListTests
{
    private static FakePlatform PlatformWithHostEnv()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables["PATH"] = "/usr/bin";
        platform.EnvironmentVariables["SOME_SECRET_TOKEN"] = "leak-me-not";
        return platform;
    }

    [Fact]
    public void NonListedVariableIsFilteredOut()
    {
        Dictionary<string, string> result = EnvAllowList.Build(PlatformWithHostEnv(), callerEnv: new Dictionary<string, string>(), passthroughAll: false);

        Assert.True(result.ContainsKey("PATH"));
        Assert.False(result.ContainsKey("SOME_SECRET_TOKEN"));
    }

    [Fact]
    public void CallerEnvEntryAlwaysPassesEvenIfNotAllowListed()
    {
        Dictionary<string, string> callerEnv = new() { ["MY_CUSTOM_FLAG"] = "1" };

        Dictionary<string, string> result = EnvAllowList.Build(PlatformWithHostEnv(), callerEnv, passthroughAll: false);

        Assert.Equal("1", result["MY_CUSTOM_FLAG"]);
    }

    [Fact]
    public void PassthroughAllDisablesFiltering()
    {
        Dictionary<string, string> result = EnvAllowList.Build(PlatformWithHostEnv(), callerEnv: new Dictionary<string, string>(), passthroughAll: true);

        Assert.True(result.ContainsKey("SOME_SECRET_TOKEN"));
    }

    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("OPENROUTER_BASE_URL")]
    [InlineData("GH_TOKEN")]
    [InlineData("MY_CUSTOM_API_KEY")]
    public void KnownPrefixesSuffixesAndExactNamesAreAllowed(string name)
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables[name] = "value";

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv: new Dictionary<string, string>(), passthroughAll: false);

        Assert.True(result.ContainsKey(name));
    }
}
