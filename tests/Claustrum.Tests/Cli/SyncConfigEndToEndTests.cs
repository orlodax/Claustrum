using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #53, #35, #45, #85 through the built binary: `sync` and `init` follow the repo's claustrum.json, `--global` does not, a bad
// config or an alias cycle is exit 2 with nothing written, `--dry-run` says which model each agent file names and what a config
// key could not be rendered, and `roles show` prints a primary role. PATH is emptied for the child: nothing here reaches a backend.
public sealed class SyncConfigEndToEndTests : IDisposable
{
    private const string Cycle = /*lang=json,strict*/ """{"models":{"frontier-coding":"a","a":"frontier-coding"}}""";

    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private readonly string emptyPath;

    public SyncConfigEndToEndTests()
    {
        cli.MarkAsGitRoot();
        emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;
    }

    public void Dispose() => cli.Dispose();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> RunAsync(params string[] args) =>
        cli.RunAsync(args, "", emptyPath, new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = null, ["OPENCODE_CONFIG_DIR"] = null, ["XDG_CONFIG_HOME"] = null });

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), json);

    private string[] Entries() => [.. Directory.GetFileSystemEntries(cli.Cwd).Select(Path.GetFileName).OfType<string>().Where(name => name != ".git").Order(StringComparer.Ordinal)];

    private static string ModelOf(string agentFile) =>
        File.ReadAllLines(agentFile).First(line => line.StartsWith("model: ", StringComparison.Ordinal))["model: ".Length..];

    [Fact]
    public async Task DryRunPrintsTheModelsSectionNamingTheModelAndWhyAsync()
    {
        WriteConfig(/*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:openrouter/vendor/model"}}""");

        CliResult result = await RunAsync("sync", "--only", "opencode", "--roles", "builder", "--dry-run");

        string stdout = Lf(result.Stdout);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("models (the model each agent file names, and why; claustrum.json or the defaults):", stdout, StringComparison.Ordinal);
        Assert.Contains("opencode builder: model openrouter/vendor/model — alias 'frontier-coding' → opencode:openrouter/vendor/model", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfigPermissionIsSaidNotRenderedPerHarnessInDryRunOnlyAsync()
    {
        WriteConfig(/*lang=json,strict*/ """{"roles":{"builder":{"permission":"readonly"}}}""");
        string[] harnesses = ["claude", "opencode", "copilot", "cursor"];

        CliResult dry = await RunAsync("sync", "--only", string.Join(',', harnesses), "--roles", "builder", "--dry-run");
        CliResult write = await RunAsync("sync", "--only", string.Join(',', harnesses), "--roles", "builder");

        string[] lines = [.. Lf(dry.Stdout).Split('\n').Where(line => line.Contains("claustrum.json permission (readonly) not rendered", StringComparison.Ordinal))];
        Assert.Equal(4, lines.Length);
        foreach (string harness in harnesses)
            Assert.Single(lines, line => line.TrimStart().StartsWith($"{harness} builder:", StringComparison.Ordinal));
        Assert.DoesNotContain("not rendered", Lf(write.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalIgnoresTheReposClaustrumJsonAsync()
    {
        WriteConfig(/*lang=json,strict*/ """{"models":{"frontier-coding":"claude:repo-only-model"}}""");

        // On Windows the home folder comes from the shell API, not HOME: without CLAUDE_CONFIG_DIR this wrote to the
        // runner's real ~/.claude (PR #109 CI, 2026-10-14).
        string claudeDir = Path.Combine(cli.Home, ".claude");
        CliResult result = await cli.RunAsync(["sync", "--global", "--only", "claude", "--roles", "builder"], "", emptyPath,
            new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = claudeDir, ["OPENCODE_CONFIG_DIR"] = null, ["XDG_CONFIG_HOME"] = null });

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string agent = Path.Combine(claudeDir, "agents", "builder.md");
        Assert.True(File.Exists(agent), result.Stdout + result.Stderr);
        Assert.Equal("opus", ModelOf(agent));
    }

    [Fact]
    public async Task AMalformedClaustrumJsonIsExitTwoAndWritesNothingAsync()
    {
        WriteConfig("{ not json");
        string[] before = Entries();

        CliResult result = await RunAsync("sync", "--only", "claude", "--roles", "builder");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(before, Entries());
    }

    public static TheoryData<string> CycleSyncCommands() =>
    [
        "sync --only claude,opencode,copilot,cursor",
        "sync --only claude,opencode --roles builder",
        "sync --only claude --dry-run",
    ];

    [Theory]
    [MemberData(nameof(CycleSyncCommands))]
    public async Task AnAliasCycleIsExitTwoAndNothingIsWrittenBySyncAsync(string command)
    {
        WriteConfig(Cycle);
        string[] before = Entries();

        CliResult result = await RunAsync(command.Split(' '));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cycle", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, Entries());
    }

    [Fact]
    public async Task AnAliasCycleIsExitTwoAndNothingIsWrittenByInitAsync()
    {
        WriteConfig(Cycle);
        string[] before = Entries();

        CliResult result = await RunAsync("init");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cycle", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, Entries());
    }

    [Fact]
    public async Task AMalformedClaustrumJsonIsExitTwoAndInitCreatesNothingAsync()
    {
        WriteConfig("{ not json");
        string[] before = Entries();

        CliResult result = await RunAsync("init");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(before, Entries());
    }

    [Fact]
    public async Task InitFollowsTheConfigAndNamesTheSameOpencodeModelAsSyncAsync()
    {
        WriteConfig(/*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:openrouter/vendor/model#high"}}""");
        Directory.CreateDirectory(Path.Combine(cli.Cwd, ".opencode"));

        CliResult init = await RunAsync("init");
        string fromInit = File.ReadAllText(Path.Combine(cli.Cwd, ".opencode", "agent", "builder.md"));
        CliResult sync = await RunAsync("sync", "--only", "opencode", "--force");
        string fromSync = File.ReadAllText(Path.Combine(cli.Cwd, ".opencode", "agent", "builder.md"));

        Assert.Equal(ExitCodes.Ok, init.ExitCode);
        Assert.Equal(ExitCodes.Ok, sync.ExitCode);
        Assert.Contains("model: openrouter/vendor/model\n", fromInit.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("variant: high\n", fromInit.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(fromInit, fromSync);
    }

    [Fact]
    public async Task RolesShowPrintsPrimaryForTheArchitectAndNotForTheBuilderAsync()
    {
        CliResult architect = await RunAsync("roles", "show", "architect");
        CliResult builder = await RunAsync("roles", "show", "builder");

        Assert.Contains("primary:     True", Lf(architect.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("primary:", Lf(builder.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADoctorRunOverAConfigNamingInjectionLoadsAndPrintsNoInjectionAsync()
    {
        WriteConfig(/*lang=json,strict*/ """{"backends":{"claude":{"injection":"claude-md"}}}""");

        CliResult result = await RunAsync("backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("injection", Lf(result.Stdout), StringComparison.OrdinalIgnoreCase);
    }
}
