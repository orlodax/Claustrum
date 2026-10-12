using Claustrum.Cli;
using Claustrum.Roles.Sync;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #83: where `sync --global` writes for each harness, and the phrase `sync` prints to say what decided it. Driven through
// FakePlatform so no real environment variable can leak in; the variables a harness does NOT honour are set on purpose
// in the cases that prove it (cursor's, above all: cursor-agent hard-codes ~/.cursor for agents, skills and mcp.json).
public sealed class SyncGlobalRootTests
{
    private static readonly string home = Path.Combine(Path.GetTempPath(), "claustrum-fake-home");

    private static FakePlatform PlatformWith(params (string Name, string? Value)[] variables)
    {
        FakePlatform platform = new() { HomeDirectory = home };
        foreach ((string name, string? value) in variables)
            platform.EnvironmentVariables[name] = value;

        return platform;
    }

    public static TheoryData<string, string> VariableByHarness => new()
    {
        { "claude", "CLAUDE_CONFIG_DIR" },
        { "opencode", "OPENCODE_CONFIG_DIR" },
        { "copilot", "COPILOT_HOME" },
    };

    [Theory]
    [MemberData(nameof(VariableByHarness))]
    public void ASetVariableDecidesTheRootAndNamesItselfAsTheReason(string harness, string variable)
    {
        string configured = Path.Combine(Path.GetTempPath(), "claustrum-configured");

        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith((variable, configured)), harness);

        Assert.Equal(configured, path);
        Assert.Equal(variable, reason);
    }

    [Theory]
    [InlineData("claude", "default; CLAUDE_CONFIG_DIR not set")]
    [InlineData("opencode", "default; OPENCODE_CONFIG_DIR and XDG_CONFIG_HOME not set")]
    [InlineData("copilot", "default; COPILOT_HOME not set")]
    public void AnUnsetVariableFallsBackToTheHarnessDefaultAndSaysSo(string harness, string expectedReason)
    {
        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(), harness);

        Assert.Equal(HarnessDefault(harness), path);
        Assert.Equal(expectedReason, reason);
    }

    // An empty or whitespace-only variable is "set to nothing", which no harness can use: it counts as unset.
    [Theory]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "")]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "   ")]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "\t")]
    [InlineData("opencode", "OPENCODE_CONFIG_DIR", "")]
    [InlineData("opencode", "OPENCODE_CONFIG_DIR", "  ")]
    [InlineData("opencode", "XDG_CONFIG_HOME", "")]
    [InlineData("opencode", "XDG_CONFIG_HOME", "  ")]
    [InlineData("copilot", "COPILOT_HOME", "")]
    [InlineData("copilot", "COPILOT_HOME", "   ")]
    public void AnEmptyOrWhitespaceVariableCountsAsUnset(string harness, string variable, string value)
    {
        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith((variable, value)), harness);

        Assert.Equal(HarnessDefault(harness), path);
        Assert.StartsWith("default; ", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullVariableIsUnsetToo()
    {
        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(("CLAUDE_CONFIG_DIR", null)), "claude");

        Assert.Equal(ClaudeSync.DefaultGlobalRoot(home), path);
        Assert.Equal("default; CLAUDE_CONFIG_DIR not set", reason);
    }

    [Fact]
    public void OpencodeConfigDirWinsOverXdgConfigHome()
    {
        string configDir = Path.Combine(Path.GetTempPath(), "oc-config");
        string xdg = Path.Combine(Path.GetTempPath(), "xdg");

        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(("OPENCODE_CONFIG_DIR", configDir), ("XDG_CONFIG_HOME", xdg)), "opencode");

        Assert.Equal(configDir, path);
        Assert.Equal("OPENCODE_CONFIG_DIR", reason);
    }

    // The XDG base directory is the parent: opencode appends its own name, so the sync root is `$XDG_CONFIG_HOME/opencode`.
    [Fact]
    public void XdgConfigHomeAloneGivesItsOpencodeSubdirectory()
    {
        string xdg = Path.Combine(Path.GetTempPath(), "xdg");

        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(("XDG_CONFIG_HOME", xdg)), "opencode");

        Assert.Equal(Path.Combine(xdg, "opencode"), path);
        Assert.Equal("XDG_CONFIG_HOME", reason);
    }

    [Fact]
    public void AnEmptyOpencodeConfigDirFallsThroughToXdgConfigHome()
    {
        string xdg = Path.Combine(Path.GetTempPath(), "xdg");

        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(("OPENCODE_CONFIG_DIR", ""), ("XDG_CONFIG_HOME", xdg)), "opencode");

        Assert.Equal(Path.Combine(xdg, "opencode"), path);
        Assert.Equal("XDG_CONFIG_HOME", reason);
    }

    [Fact]
    public void ClaudeAndCopilotIgnoreEachOthersVariablesAndOpencodes()
    {
        FakePlatform platform = PlatformWith(
            ("CLAUDE_CONFIG_DIR", "/c"), ("COPILOT_HOME", "/p"), ("OPENCODE_CONFIG_DIR", "/o"), ("XDG_CONFIG_HOME", "/x"));

        Assert.Equal("/c", SyncGlobalRoot.Resolve(platform, "claude").Path);
        Assert.Equal("/p", SyncGlobalRoot.Resolve(platform, "copilot").Path);
        Assert.Equal("/o", SyncGlobalRoot.Resolve(platform, "opencode").Path);
    }

    // cursor-agent 2026.09.18 reads agents, skills and mcp.json from ~/.cursor whatever these say (NOTES.md #83): honouring
    // them would write files it never loads.
    [Fact]
    public void CursorIgnoresItsConfigDirXdgConfigHomeAndEveryOtherHarnessVariable()
    {
        FakePlatform platform = PlatformWith(
            ("CURSOR_CONFIG_DIR", "/cursor-config"), ("XDG_CONFIG_HOME", "/xdg"), ("CLAUDE_CONFIG_DIR", "/c"),
            ("OPENCODE_CONFIG_DIR", "/o"), ("COPILOT_HOME", "/p"));

        (string path, string reason) = SyncGlobalRoot.Resolve(platform, "cursor");

        Assert.Equal(CursorSync.DefaultGlobalRoot(home), path);
        Assert.Equal(Path.Combine(home, ".cursor"), path);
        Assert.Contains("~/.cursor", reason, StringComparison.Ordinal);
        Assert.Contains("CURSOR_CONFIG_DIR", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CursorWithNothingSetResolvesTheSameRootAndReason()
    {
        (string path, string reason) = SyncGlobalRoot.Resolve(PlatformWith(), "cursor");
        (string withVariables, string sameReason) = SyncGlobalRoot.Resolve(PlatformWith(("CURSOR_CONFIG_DIR", "/elsewhere")), "cursor");

        Assert.Equal(Path.Combine(home, ".cursor"), path);
        Assert.Equal(path, withVariables);
        Assert.Equal(reason, sameReason);
    }

    [Theory]
    [InlineData("windsurf")]
    [InlineData("")]
    [InlineData("Claude")]
    [InlineData("claude,opencode")]
    public void AnUnknownHarnessThrowsNamingIt(string harness)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => SyncGlobalRoot.Resolve(PlatformWith(), harness));

        Assert.Equal("harness", ex.ParamName);
    }

    // Spelled out here as literals, so a change to a `DefaultGlobalRoot` (the one place each default lives) cannot move the
    // resolver and the sync's own fallback together without a test noticing.
    [Fact]
    public void TheDefaultsAreTheDirectoriesEachHarnessReadsUnderTheHome()
    {
        FakePlatform platform = PlatformWith();

        Assert.Equal(Path.Combine(home, ".claude"), SyncGlobalRoot.Resolve(platform, "claude").Path);
        Assert.Equal(Path.Combine(home, ".config", "opencode"), SyncGlobalRoot.Resolve(platform, "opencode").Path);
        Assert.Equal(Path.Combine(home, ".copilot"), SyncGlobalRoot.Resolve(platform, "copilot").Path);
        Assert.Equal(Path.Combine(home, ".cursor"), SyncGlobalRoot.Resolve(platform, "cursor").Path);
    }

    private static string HarnessDefault(string harness) => harness switch
    {
        "claude" => ClaudeSync.DefaultGlobalRoot(home),
        "opencode" => OpencodeSync.DefaultGlobalRoot(home),
        "copilot" => CopilotSync.DefaultGlobalRoot(home),
        "cursor" => CursorSync.DefaultGlobalRoot(home),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null),
    };
}
