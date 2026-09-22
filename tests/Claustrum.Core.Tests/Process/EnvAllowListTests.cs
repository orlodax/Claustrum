using Claustrum.Core.Jobs;
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

    // A job tree is handed out by a coordinator, never forwarded by a member — a member enrolled in
    // its parent's tree by inheritance would reserve the tree's cap for its whole lifetime and leave
    // its own children $0 (EnvAllowList's own ⚠). passthroughAll drops this even though it drops
    // nothing else, because it is otherwise an unconditional passthrough of the host environment.
    [Fact]
    public void PassthroughAllStillDropsAnInheritedParentJobVariable()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables[BudgetLedger.TreeVariable] = "inherited-tree-id";
        platform.EnvironmentVariables["PATH"] = "/usr/bin";

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv: new Dictionary<string, string>(), passthroughAll: true);

        Assert.False(result.ContainsKey(BudgetLedger.TreeVariable));
        Assert.True(result.ContainsKey("PATH"));
    }

    // `coordinate` sets CLAUSTRUM_PARENT_JOB deliberately, on the architect's own request env
    // (CoordinatePlan.TreeEnv) — the caller always wins over both rules, allow-list and passthroughAll
    // alike (EnvAllowList's own ⚠ "caller-supplied values win over both rules").
    [Fact]
    public void ACallerSuppliedParentJobIsKeptEvenUnderPassthroughAll()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables[BudgetLedger.TreeVariable] = "inherited-tree-id";
        Dictionary<string, string> callerEnv = new() { [BudgetLedger.TreeVariable] = "deliberate-tree-id" };

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv, passthroughAll: true);

        Assert.Equal("deliberate-tree-id", result[BudgetLedger.TreeVariable]);
    }

    [Fact]
    public void ACallerSuppliedParentJobIsKeptUnderTheOrdinaryAllowListToo()
    {
        FakePlatform platform = new();
        Dictionary<string, string> callerEnv = new() { [BudgetLedger.TreeVariable] = "deliberate-tree-id" };

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv, passthroughAll: false);

        Assert.Equal("deliberate-tree-id", result[BudgetLedger.TreeVariable]);
    }
}
