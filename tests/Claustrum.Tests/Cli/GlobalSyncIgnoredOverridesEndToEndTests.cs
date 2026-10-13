using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #99 through the built binary: `sync --global` renders the library alone, so a repo's `.claustrum/roles/` override does not
// reach the user-global agents, and the CLI says so — one header and the role names, in write, --dry-run and --check alike,
// listed once however many harnesses sync it, never an error (exit code unchanged), and never in repo mode. The child sees
// only the config-dir variables a case sets; writes go to scratch roots. stdout has `\r\n` folded to `\n`.
public sealed class GlobalSyncIgnoredOverridesEndToEndTests : IDisposable
{
    private const string Header = "ignored local overrides (--global renders the library only; a repo's .claustrum/roles/ applies to that repo):";
    private const string OverrideBody = "LOCAL-OVERRIDE-BODY";

    private static readonly string[] resolverVariables =
        ["CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME", "COPILOT_HOME", "CURSOR_CONFIG_DIR"];

    private readonly ClaustrumCli cli = new();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-ignored-overrides-e2e-").FullName;

    public void Dispose()
    {
        cli.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string ClaudeRoot => Path.Combine(scratch, "claude-config");

    private string OpencodeRoot => Path.Combine(scratch, "opencode-config");

    private void WriteOverride(string role)
    {
        string directory = Directory.CreateDirectory(Path.Combine(cli.Cwd, ".claustrum", "roles", role)).FullName;
        File.WriteAllText(Path.Combine(directory, "ROLE.md"), $"{OverrideBody} for {role}\n");
    }

    private Task<CliResult> SyncAsync(string[] args, bool global = true)
    {
        Dictionary<string, string?> env = resolverVariables.ToDictionary(name => name, _ => (string?)null);
        env["CLAUDE_CONFIG_DIR"] = ClaudeRoot;
        env["OPENCODE_CONFIG_DIR"] = OpencodeRoot;
        string[] scope = global ? ["--global"] : [];
        return cli.RunAsync(["sync", .. scope, .. args], "", null, env);
    }

    private static string[] Flags(string flag) => flag.Length == 0 ? [] : [flag];

    /// <summary>The indented lines under the ignored-overrides header, which must be there.</summary>
    private static string[] IgnoredSection(CliResult result)
    {
        string[] lines = Lf(result.Stdout).Split('\n');
        int start = Array.IndexOf(lines, Header);
        Assert.True(start >= 0, $"no ignored-overrides header in:\n{result.Stdout}");
        return [.. lines[(start + 1)..].TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))];
    }

    private static int Occurrences(CliResult result, string line) => Lf(result.Stdout).Split('\n').Count(candidate => candidate == line);

    [Fact]
    public async Task AGlobalSyncWritesTheLibraryRenderNamesTheIgnoredRoleAndExitsZeroAsync()
    {
        WriteOverride("builder");

        CliResult result = await SyncAsync(["--only", "claude", "--roles", "builder"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(["  builder"], IgnoredSection(result));
        string global = File.ReadAllText(Path.Combine(ClaudeRoot, "agents", "builder.md"));
        Assert.DoesNotContain(OverrideBody, global, StringComparison.Ordinal);
        Assert.DoesNotContain("source=local", global, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claude")), "--global writes nothing into the repo");
    }

    [Fact]
    public async Task DryRunPrintsTheSameHeaderAndNoOverrideTextInTheProposedDiffAsync()
    {
        WriteOverride("builder");

        CliResult result = await SyncAsync(["--only", "claude", "--roles", "builder", "--dry-run"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(["  builder"], IgnoredSection(result));
        Assert.DoesNotContain(OverrideBody, Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(Directory.Exists(ClaudeRoot));
    }

    [Fact]
    public async Task CheckPrintsTheSameHeaderAndKeepsItsExitCodeAsync()
    {
        WriteOverride("builder");
        CliResult fresh = await SyncAsync(["--only", "claude", "--roles", "builder", "--check"]);
        Assert.Equal(ExitCodes.Usage, fresh.ExitCode); // the files are missing, as they would be with no override at all
        Assert.Equal(["  builder"], IgnoredSection(fresh));

        Assert.Equal(ExitCodes.Ok, (await SyncAsync(["--only", "claude", "--roles", "builder"])).ExitCode);
        CliResult synced = await SyncAsync(["--only", "claude", "--roles", "builder", "--check"]);

        Assert.Equal(ExitCodes.Ok, synced.ExitCode);
        Assert.Equal(["  builder"], IgnoredSection(synced));
    }

    [Fact]
    public async Task TwoHarnessesThatBothSyncTheRoleListItOnceAsync()
    {
        WriteOverride("builder");

        CliResult result = await SyncAsync(["--only", "claude,opencode", "--roles", "builder"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(["  builder"], IgnoredSection(result));
        Assert.Equal(1, Occurrences(result, Header));
        Assert.True(File.Exists(Path.Combine(ClaudeRoot, "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(OpencodeRoot, "agent", "builder.md")));
        Assert.DoesNotContain(OverrideBody, File.ReadAllText(Path.Combine(OpencodeRoot, "agent", "builder.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheRolesThatHaveAnOverrideAreListedAsync()
    {
        WriteOverride("builder");

        CliResult result = await SyncAsync(["--only", "claude", "--roles", "builder,tester"]);

        Assert.Equal(["  builder"], IgnoredSection(result));
    }

    [Fact]
    public async Task ARepoWithNoOverridePrintsNoHeaderUnderGlobalAsync()
    {
        CliResult result = await SyncAsync(["--only", "claude", "--roles", "builder"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("ignored local overrides", Lf(result.Stdout), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task RepoModeNeverPrintsTheHeaderAndRendersTheOverrideAsync(string flag)
    {
        WriteOverride("builder");

        CliResult result = await SyncAsync(["--only", "claude", "--roles", "builder", .. Flags(flag)], global: false);

        Assert.DoesNotContain("ignored local overrides", Lf(result.Stdout), StringComparison.Ordinal);
        if (flag.Length == 0)
        {
            Assert.Equal(ExitCodes.Ok, result.ExitCode);
            Assert.Contains(OverrideBody, File.ReadAllText(Path.Combine(cli.Cwd, ".claude", "agents", "builder.md")), StringComparison.Ordinal);
        }
    }
}
