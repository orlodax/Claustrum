using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #97 through the built binary: a role override (`.claustrum/roles/<role>/ROLE.md`, `role.json`, `parts/*.md`) whose real path
// leaves the working directory is never read. Every verb that loads the role refuses with exit 2 and a message naming the
// link and where it leads, nothing from the target reaches stdout or stderr, and nothing is written. Paths are matched
// case-insensitively on Windows and stdout/stderr have `\r\n` folded to `\n`.
public sealed class RoleOverrideLinkEndToEndTests : IDisposable
{
    private const string Secret = "TOP-SECRET-TOKEN";
    private const string Refusal = "resolves outside the working directory";

    private static readonly StringComparison pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-role-link-e2e-").FullName;
    private readonly string outside;
    private readonly string secret;

    public RoleOverrideLinkEndToEndTests()
    {
        outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
        secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, $"{Secret}\n");
    }

    public void Dispose()
    {
        cli.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string OverridePath(params string[] relative) => Path.Combine([cli.Cwd, ".claustrum", "roles", "builder", .. relative]);

    // The repo-relative tail of an override file: the child may print the temp directory in another spelling than the test typed.
    private static string OverrideTail(params string[] relative) => Path.Combine([".claustrum", "roles", "builder", .. relative]);

    private static string[] Flags(string flag) => flag.Length == 0 ? [] : [flag];

    private void LinkOverride(params string[] relative)
    {
        string link = OverridePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link) ?? cli.Cwd);
        TestLinks.Create(link, secret);
    }

    private static string[] Snapshot(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private void AssertRefusedWithoutLeaking(CliResult result, string linkTail)
    {
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(Refusal, Lf(result.Stderr), StringComparison.Ordinal);
        Assert.Contains(linkTail, Lf(result.Stderr), pathComparison);
        Assert.Contains(secret, Lf(result.Stderr), pathComparison);
        Assert.DoesNotContain(Secret, Lf(result.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Lf(result.Stderr), StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    private Task<CliResult> SyncAsync(params string[] args) => cli.RunAsync(["sync", "--only", "claude", "--roles", "builder", .. args], "", null);

    // `sync` processes roles alphabetically and `architect` renders before `builder`, so the case is restricted to `builder`:
    // the refusal is then the first thing the sync meets.
    [Theory]
    [InlineData("")]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task ALinkedRoleMdLeavingTheRepoIsRefusedInEveryModeAndNeverReadAsync(string flag)
    {
        LinkOverride("ROLE.md");
        string[] before = Snapshot(outside);

        CliResult result = await SyncAsync(Flags(flag));

        AssertRefusedWithoutLeaking(result, OverrideTail("ROLE.md"));
        Assert.False(File.Exists(Path.Combine(cli.Cwd, ".claude", "agents", "builder.md")));
        Assert.Equal(before, Snapshot(outside));
        Assert.Equal($"{Secret}\n", File.ReadAllText(secret));
    }

    [Theory]
    [InlineData("role.json")]
    [InlineData("parts/delegation.claude.md")]
    [InlineData("parts/delegation.default.md")]
    public async Task ALinkedRoleJsonOrPartLeavingTheRepoIsRefusedByASyncAsync(string relative)
    {
        string[] parts = relative.Split('/');
        LinkOverride(parts);

        CliResult result = await SyncAsync();

        AssertRefusedWithoutLeaking(result, OverrideTail(parts));
        Assert.False(File.Exists(Path.Combine(cli.Cwd, ".claude", "agents", "builder.md")));
    }

    [Fact]
    public async Task RolesShowRefusesALinkedOverrideWithTheMessageAndNothingOnStdoutAsync()
    {
        LinkOverride("ROLE.md");

        CliResult result = await cli.RunAsync(["roles", "show", "builder"], "", null);

        AssertRefusedWithoutLeaking(result, OverrideTail("ROLE.md"));
        Assert.Equal("", result.Stdout.Trim());
    }

    // `roles list` loads every role and has no handler of its own: the process door maps the refusal to the same exit 2.
    [Fact]
    public async Task RolesListRefusesALinkedOverrideThroughTheProcessDoorAsync()
    {
        LinkOverride("role.json");

        CliResult result = await cli.RunAsync(["roles", "list"], "", null);

        AssertRefusedWithoutLeaking(result, OverrideTail("role.json"));
    }

    // The same fence on the delegation path: the role is loaded before any job is minted, so nothing is created under the home.
    [Fact]
    public async Task RunRefusesALinkedOverrideBeforeAnyJobIsMintedAsync()
    {
        LinkOverride("ROLE.md");

        CliResult result = await cli.RunAsync(["run", "builder", "--brief", "do the task", "--json"], "", null);

        AssertRefusedWithoutLeaking(result, OverrideTail("ROLE.md"));
        Assert.False(Directory.Exists(cli.JobsDirectory) && Directory.GetDirectories(cli.JobsDirectory).Length > 0);
    }

    [Fact]
    public async Task AClaustrumDirectoryLinkedOutsideHoldingAnOverrideIsRefusedAsync()
    {
        string held = Directory.CreateDirectory(Path.Combine(outside, "roles", "builder")).FullName;
        File.WriteAllText(Path.Combine(held, "ROLE.md"), $"{Secret}\n");
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claustrum"), outside);

        CliResult result = await cli.RunAsync(["roles", "show", "builder"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(Refusal, Lf(result.Stderr), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(held, "ROLE.md"), Lf(result.Stderr), pathComparison);
        Assert.DoesNotContain(Secret, Lf(result.Stdout) + Lf(result.Stderr), StringComparison.Ordinal);
    }

    // The fence acts on an override file that is there: a `.claustrum` link out with none behind it leaves the verbs working.
    [Fact]
    public async Task AClaustrumDirectoryLinkedOutsideWithNoOverrideFileLeavesRolesShowWorkingAsync()
    {
        File.WriteAllText(Path.Combine(outside, "unrelated.txt"), $"{Secret}\n");
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claustrum"), outside);

        CliResult result = await cli.RunAsync(["roles", "show", "builder"], "", null);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("source:      library", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Lf(result.Stdout) + Lf(result.Stderr), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkedRoleMdThatStaysInsideTheRepoIsReadThroughAndSyncedAsync()
    {
        string real = Directory.CreateDirectory(Path.Combine(cli.Cwd, "real")).FullName;
        File.WriteAllText(Path.Combine(real, "ROLE.md"), "You are the LINKED-INSIDE builder.\n");
        Directory.CreateDirectory(Path.GetDirectoryName(OverridePath("ROLE.md")) ?? cli.Cwd);
        TestLinks.Create(OverridePath("ROLE.md"), Path.Combine(real, "ROLE.md"));

        CliResult result = await SyncAsync();
        CliResult show = await cli.RunAsync(["roles", "show", "builder"], "", null);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string agent = File.ReadAllText(Path.Combine(cli.Cwd, ".claude", "agents", "builder.md"));
        Assert.Contains("You are the LINKED-INSIDE builder.", agent, StringComparison.Ordinal);
        Assert.Contains("source=local", agent, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Ok, show.ExitCode);
        Assert.Contains("source:      local", Lf(show.Stdout), StringComparison.Ordinal);
    }
}
