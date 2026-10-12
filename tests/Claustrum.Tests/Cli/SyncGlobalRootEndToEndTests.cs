using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #83 through the built binary: `sync --global` writes where the harness reads, and says so with one
// `global root (<harness>): <path> (<reason>)` line on stdout in write, --dry-run and --check alike, never in repo mode.
// The child sees only the variables a case sets: every config-dir variable the resolver reads is removed first, so the
// developer's own export cannot decide the answer. Writes go to relocated roots under a scratch directory; the default
// root and cursor (which ignores its variable) are only ever dry-run, because on Windows the home cannot be redirected
// and a write would land in the real profile. stdout is compared with `\r\n` folded to `\n` (Console.WriteLine on Windows).
public sealed class SyncGlobalRootEndToEndTests : IDisposable
{
    private static readonly string[] resolverVariables =
        ["CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME", "COPILOT_HOME", "CURSOR_CONFIG_DIR"];

    private readonly ClaustrumCli cli = new();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-global-root-e2e-").FullName;

    public void Dispose()
    {
        cli.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string Relocated(string name) => Path.Combine(scratch, name);

    private Task<CliResult> SyncAsync(IEnumerable<(string Name, string? Value)> variables, params string[] args)
    {
        Dictionary<string, string?> env = resolverVariables.ToDictionary(name => name, _ => (string?)null);
        foreach ((string name, string? value) in variables)
            env[name] = value;

        return cli.RunAsync(["sync", .. args], "", null, env);
    }

    private static string[] GlobalRootLines(CliResult result) =>
        [.. Lf(result.Stdout).Split('\n').Where(line => line.StartsWith("global root (", StringComparison.Ordinal))];

    [Fact]
    public async Task ClaudeConfigDirDecidesTheRootIsPrintedAndReceivesEveryFileAsync()
    {
        string root = Relocated("claude-config");

        CliResult result = await SyncAsync([("CLAUDE_CONFIG_DIR", root)], "--global", "--only", "claude", "--roles", "builder");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal([$"global root (claude): {root} (CLAUDE_CONFIG_DIR)"], GlobalRootLines(result));
        Assert.StartsWith($"global root (claude): {root} (CLAUDE_CONFIG_DIR)\n", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(root, "agents", "builder-xhigh.md")));
        Assert.True(File.Exists(Path.Combine(root, "skills", "claustrum", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(cli.Home, ".claude")));
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claude")));
        Assert.DoesNotContain("global root", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeConfigDirBeatsXdgConfigHomeAndXdgAloneGivesItsOpencodeSubdirectoryAsync()
    {
        string configDir = Relocated("oc-config");
        string xdg = Relocated("xdg");

        CliResult both = await SyncAsync([("OPENCODE_CONFIG_DIR", configDir), ("XDG_CONFIG_HOME", xdg)], "--global", "--only", "opencode", "--roles", "builder");

        Assert.Equal([$"global root (opencode): {configDir} (OPENCODE_CONFIG_DIR)"], GlobalRootLines(both));
        Assert.True(File.Exists(Path.Combine(configDir, "agent", "builder.md")));
        Assert.False(Directory.Exists(Path.Combine(xdg, "opencode")));

        CliResult xdgOnly = await SyncAsync([("XDG_CONFIG_HOME", xdg)], "--global", "--only", "opencode", "--roles", "builder");

        Assert.Equal([$"global root (opencode): {Path.Combine(xdg, "opencode")} (XDG_CONFIG_HOME)"], GlobalRootLines(xdgOnly));
        Assert.True(File.Exists(Path.Combine(xdg, "opencode", "agent", "builder.md")));
    }

    [Fact]
    public async Task CopilotHomeDecidesTheRootAndTheAgentFileLandsThereAsync()
    {
        string root = Relocated("copilot-home");

        CliResult result = await SyncAsync([("COPILOT_HOME", root)], "--global", "--only", "copilot", "--roles", "builder");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal([$"global root (copilot): {root} (COPILOT_HOME)"], GlobalRootLines(result));
        Assert.True(File.Exists(Path.Combine(root, "agents", "builder.agent.md")));
        Assert.False(Directory.Exists(Path.Combine(cli.Home, ".copilot")));
    }

    // cursor-agent hard-codes ~/.cursor for agents, skills and mcp.json (NOTES.md #83): CURSOR_CONFIG_DIR and XDG_CONFIG_HOME move nothing here.
    [Fact]
    public async Task CursorKeepsItsHomeDirectoryWhateverItsConfigDirVariableSaysAsync()
    {
        string decoy = Relocated("cursor-config");

        CliResult result = await SyncAsync([("CURSOR_CONFIG_DIR", decoy), ("XDG_CONFIG_HOME", Relocated("xdg"))], "--global", "--only", "cursor", "--roles", "builder", "--dry-run");

        string line = Assert.Single(GlobalRootLines(result));
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.StartsWith("global root (cursor): ", line, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}.cursor (cursor-agent reads agents, skills and mcp.json from ~/.cursor; CURSOR_CONFIG_DIR moves only cli-config.json)", line, StringComparison.Ordinal);
        Assert.DoesNotContain(decoy, line, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.StartsWith($"global root (cursor): {Path.Combine(cli.Home, ".cursor")} (", line, StringComparison.Ordinal);

        Assert.False(Directory.Exists(decoy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnUnsetOrEmptyVariableSaysTheDefaultAndNamesTheVariableItWouldHaveReadAsync(string? value)
    {
        CliResult result = await SyncAsync([("CLAUDE_CONFIG_DIR", value)], "--global", "--only", "claude", "--roles", "builder", "--dry-run");

        string line = Assert.Single(GlobalRootLines(result));
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}.claude (default; CLAUDE_CONFIG_DIR not set)", line, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal($"global root (claude): {Path.Combine(cli.Home, ".claude")} (default; CLAUDE_CONFIG_DIR not set)", line);

        Assert.False(Directory.Exists(Path.Combine(cli.Home, ".claude")));
    }

    [Fact]
    public async Task EveryHarnessNamedByOnlyPrintsItsOwnLineInTheOrderGivenAsync()
    {
        CliResult result = await SyncAsync(
            [("CLAUDE_CONFIG_DIR", Relocated("c")), ("COPILOT_HOME", Relocated("p")), ("OPENCODE_CONFIG_DIR", Relocated("o"))],
            "--global", "--only", "copilot,claude,opencode", "--roles", "builder", "--dry-run");

        string[] lines = GlobalRootLines(result);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("global root (copilot): ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("global root (claude): ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("global root (opencode): ", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunPrintsTheLineAndDiffsTheFilesWithoutCreatingTheRootAsync()
    {
        string root = Relocated("claude-config");

        CliResult result = await SyncAsync([("CLAUDE_CONFIG_DIR", root)], "--global", "--only", "claude", "--roles", "builder", "--dry-run");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal([$"global root (claude): {root} (CLAUDE_CONFIG_DIR)"], GlobalRootLines(result));
        Assert.Contains("--- /dev/null", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CheckPrintsTheLineExitsTwoOnAnEmptyRootAndZeroOnceItIsWrittenAsync()
    {
        string root = Relocated("claude-config");
        string[] args = ["--global", "--only", "claude", "--roles", "builder"];

        CliResult missing = await SyncAsync([("CLAUDE_CONFIG_DIR", root)], [.. args, "--check"]);

        Assert.Equal(ExitCodes.Usage, missing.ExitCode);
        Assert.Equal([$"global root (claude): {root} (CLAUDE_CONFIG_DIR)"], GlobalRootLines(missing));
        Assert.Contains($"missing: {Path.Combine(root, "agents", "builder.md")}", Lf(missing.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));

        Assert.Equal(ExitCodes.Ok, (await SyncAsync([("CLAUDE_CONFIG_DIR", root)], args)).ExitCode);
        CliResult clean = await SyncAsync([("CLAUDE_CONFIG_DIR", root)], [.. args, "--check"]);

        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Equal([$"global root (claude): {root} (CLAUDE_CONFIG_DIR)"], GlobalRootLines(clean));
    }

    // Repo mode has no global root: printing a line there would claim a place the run never writes to.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public async Task RepoModePrintsNoLineAndIgnoresTheVariablesAsync(string harness)
    {
        CliResult result = await SyncAsync(
            [("CLAUDE_CONFIG_DIR", Relocated("c")), ("OPENCODE_CONFIG_DIR", Relocated("o")), ("COPILOT_HOME", Relocated("p")), ("XDG_CONFIG_HOME", Relocated("x"))],
            "--only", harness, "--roles", "builder");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Empty(GlobalRootLines(result));
        Assert.DoesNotContain("global root", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task RepoModeDryRunAndCheckPrintNoLineEitherAsync(string mode)
    {
        CliResult result = await SyncAsync([("CLAUDE_CONFIG_DIR", Relocated("c"))], "--only", "claude", "--roles", "builder", mode);

        Assert.Empty(GlobalRootLines(result));
        Assert.DoesNotContain("global root", Lf(result.Stdout), StringComparison.Ordinal);
    }
}
