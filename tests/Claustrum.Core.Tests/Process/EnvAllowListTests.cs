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

    // #57: a delegated claude reads the caller's login, CLAUDE.md and agents through CLAUDE_CONFIG_DIR.
    // It is allowed by exact name, not as a CLAUDE_ prefix, which would also forward a parent Claude
    // Code session's identity and messaging token (28 CLAUDE_ names counted in one session, 23 of
    // them CLAUDE_CODE_*; NOTES.md "CLAUDE_CONFIG_DIR by exact name").
    [Fact]
    public void ClaudeConfigDirPassesByExactName()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "/home/me/.claude-work";

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv: new Dictionary<string, string>(), passthroughAll: false);

        Assert.Equal("/home/me/.claude-work", result["CLAUDE_CONFIG_DIR"]);
    }

    [Theory]
    [InlineData("CLAUDE_CODE_SESSION_ID")]
    [InlineData("CLAUDE_CODE_CHILD_SESSION")]
    [InlineData("CLAUDE_CODE_ENTRYPOINT")]
    [InlineData("CLAUDE_CODE_MESSAGING_TOKEN")]
    [InlineData("CLAUDE_CONFIG_DIRECTORY")]
    [InlineData("CLAUDE_CONFIG_DIR_EXTRA")]
    public void OtherClaudeVariablesAreNotForwarded(string name)
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "/home/me/.claude-work";
        platform.EnvironmentVariables[name] = "parent-session-identity";

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv: new Dictionary<string, string>(), passthroughAll: false);

        Assert.False(result.ContainsKey(name));
        Assert.True(result.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void ClaudeConfigDirPassesUnderPassthroughAllWhileTheTreeVariableStillDoesNot()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = "/home/me/.claude-work";
        platform.EnvironmentVariables[BudgetLedger.TreeVariable] = "inherited-tree-id";

        Dictionary<string, string> result = EnvAllowList.Build(platform, callerEnv: new Dictionary<string, string>(), passthroughAll: true);

        Assert.Equal("/home/me/.claude-work", result["CLAUDE_CONFIG_DIR"]);
        Assert.False(result.ContainsKey(BudgetLedger.TreeVariable));
    }
}
