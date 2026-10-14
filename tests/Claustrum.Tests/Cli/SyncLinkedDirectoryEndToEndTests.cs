using System.Text.RegularExpressions;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #95, #93 through the built binary: in repo mode a target resolving outside the cwd through a linked directory is listed
// under `refused (...)` with its real path, exit 2 in write, --dry-run and --check, --force or not, and nothing is created
// outside; a link that stays inside is written through and annotated. Under --global nothing is refused and a linked root
// prints `{path} → {real}`. Paths are matched by their repo-relative tail (Windows may print the temp directory in another
// spelling) and stdout has `\r\n` folded to `\n`. The dotfiles layout is built under scratch directories, never `~`.
public sealed partial class SyncLinkedDirectoryEndToEndTests : IDisposable
{
    private const string RefusedHeader = "refused (sync never writes through a link that leaves the repo, nor into a pipe or socket):";

    private static readonly string agentTail = Path.Combine(".claude", "agents", "builder.md");
    private static readonly string[] resolverVariables =
        ["CLAUDE_CONFIG_DIR", "OPENCODE_CONFIG_DIR", "XDG_CONFIG_HOME", "COPILOT_HOME", "CURSOR_CONFIG_DIR"];

    private static readonly StringComparison pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ClaustrumCli cli = new();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-linked-dir-e2e-").FullName;
    private readonly string outside;

    public SyncLinkedDirectoryEndToEndTests()
    {
        outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine\n");
    }

    public void Dispose()
    {
        cli.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] Lines(CliResult result) => Lf(result.Stdout).Split('\n');

    /// <summary>The indented lines under a section header such as `written:`, empty when there is none.</summary>
    private static string[] Section(CliResult result, string header)
    {
        string[] lines = Lines(result);
        int start = Array.IndexOf(lines, header);
        return start < 0 ? [] : [.. lines[(start + 1)..].TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))];
    }

    private static void AssertNoCrash(CliResult result)
    {
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    private static string[] Snapshot(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private Task<CliResult> SyncAsync(params string[] args) => cli.RunAsync(["sync", "--only", "claude", "--roles", "builder", .. args], "", null);

    private Task<CliResult> GlobalSyncAsync(string claudeConfigDir, params string[] args)
    {
        Dictionary<string, string?> env = resolverVariables.ToDictionary(name => name, _ => (string?)null);
        env["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
        return cli.RunAsync(["sync", "--global", "--only", "claude", "--roles", "builder", .. args], "", null, env);
    }

    private string Scratch(params string[] parts) => Path.Combine([scratch, .. parts]);

    private static string[] Flags(string flag) => flag.Length == 0 ? [] : [flag];

    [Theory]
    [InlineData("")]
    [InlineData("--force")]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task ALinkedClaudeDirectoryOutsideTheRepoIsRefusedWithItsRealPathInEveryModeAsync(string flag)
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claude"), outside);
        string[] before = Snapshot(outside);

        CliResult result = await SyncAsync(Flags(flag));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string[] refused = Section(result, RefusedHeader);
        string line = Assert.Single(refused, entry => entry.Contains(agentTail, StringComparison.Ordinal));
        Assert.Contains(" → ", line, StringComparison.Ordinal);
        Assert.EndsWith($" → {Path.Combine(outside, "agents", "builder.md")}", line, pathComparison);
        Assert.Contains(refused, entry => entry.Contains(Path.Combine(".claude", "skills", "claustrum", "SKILL.md"), StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(result), entry => entry.Contains(agentTail, StringComparison.Ordinal) && !refused.Contains(entry));
        Assert.Equal(before, Snapshot(outside));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task ALinkedClaudeDirectoryInsideTheRepoIsWrittenThroughAndAnnotatedAsync()
    {
        string inside = Directory.CreateDirectory(Path.Combine(cli.Cwd, "real-claude")).FullName;
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claude"), inside);

        CliResult result = await SyncAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string written = Assert.Single(Section(result, "written:"), entry => entry.Contains(agentTail, StringComparison.Ordinal));
        Assert.Contains(" (resolved → ", written, StringComparison.Ordinal);
        Assert.EndsWith($"{Path.Combine("real-claude", "agents", "builder.md")})", written, pathComparison);
        Assert.DoesNotContain("refused", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(inside, "agents", "builder.md")));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task DryRunOverALinkThatStaysInsideTheRepoPrintsResolvedBeforeTheDiffAsync()
    {
        string inside = Directory.CreateDirectory(Path.Combine(cli.Cwd, "real-claude")).FullName;
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claude"), inside);

        CliResult result = await SyncAsync("--dry-run");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string[] lines = Lines(result);
        int resolved = Array.FindIndex(lines, line => line.StartsWith($"resolved {agentTail} → ", StringComparison.Ordinal));
        Assert.True(resolved >= 0, "no 'resolved' line");
        Assert.EndsWith(Path.Combine("real-claude", "agents", "builder.md"), lines[resolved], pathComparison);
        Assert.Equal("--- /dev/null", lines[resolved + 1]);
        Assert.False(Directory.Exists(Path.Combine(inside, "agents")));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task GlobalSyncThroughALinkedConfigDirPrintsTheRealPathRefusesNothingAndWritesThereAsync()
    {
        string real = Directory.CreateDirectory(Scratch("real-config")).FullName;
        string link = Scratch("linked-config");
        TestLinks.CreateDirectoryLink(link, real);

        CliResult result = await GlobalSyncAsync(link);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal($"global root (claude): {link} → {real} (CLAUDE_CONFIG_DIR)", Lines(result)[0]);
        Assert.True(File.Exists(Path.Combine(real, "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(real, "skills", "claustrum", "SKILL.md")));
        Assert.DoesNotContain("refused", Lf(result.Stdout), StringComparison.Ordinal);
        string written = Assert.Single(Section(result, "written:"), entry => entry.Contains(Path.Combine("agents", "builder.md"), StringComparison.Ordinal) && entry.Contains(" (resolved → ", StringComparison.Ordinal));
        Assert.EndsWith($"{Path.Combine(real, "agents", "builder.md")})", written, pathComparison);
        AssertNoCrash(result);
    }

    [Fact]
    public async Task GlobalSyncIntoALinkedAgentsDirectoryTheStowCaseWritesThroughItAndAnnotatesAsync()
    {
        string root = Directory.CreateDirectory(Scratch("stow-root")).FullName;
        string store = Directory.CreateDirectory(Scratch("stow-store", "agents")).FullName;
        TestLinks.CreateDirectoryLink(Path.Combine(root, "agents"), store);

        CliResult result = await GlobalSyncAsync(root);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal($"global root (claude): {root} (CLAUDE_CONFIG_DIR)", Lines(result)[0]);
        Assert.True(File.Exists(Path.Combine(store, "builder.md")));
        string written = Assert.Single(Section(result, "written:"), entry => entry.Contains(Path.Combine("agents", "builder.md"), StringComparison.Ordinal));
        Assert.EndsWith($" (resolved → {Path.Combine(store, "builder.md")})", written, pathComparison);
        Assert.DoesNotContain("refused", Lf(result.Stdout), StringComparison.Ordinal);
        AssertNoCrash(result);
    }

    [Fact]
    public async Task GlobalForceReplacingALeafLinkInsideALinkedAgentsDirectorySaysWhereItLandsAsync()
    {
        string root = Directory.CreateDirectory(Scratch("stow-root")).FullName;
        string store = Directory.CreateDirectory(Scratch("stow-store", "agents")).FullName;
        string notes = Directory.CreateDirectory(Scratch("notes")).FullName;
        File.WriteAllText(Path.Combine(notes, "builder.md"), "mine\n");
        TestLinks.CreateDirectoryLink(Path.Combine(root, "agents"), store);
        TestLinks.Create(Path.Combine(store, "builder.md"), Path.Combine(notes, "builder.md"));

        CliResult result = await GlobalSyncAsync(root, "--force");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string line = Assert.Single(Section(result, "replaced symlink (the link target was left untouched):"), entry => entry.Contains(Path.Combine("agents", "builder.md"), StringComparison.Ordinal));
        Assert.Contains(" → ", line, StringComparison.Ordinal);
        Assert.EndsWith($" (lands at {Path.Combine(store, "builder.md")})", line, pathComparison);
        Assert.Null(new FileInfo(Path.Combine(store, "builder.md")).LinkTarget);
        Assert.Contains("claustrum:generated", File.ReadAllText(Path.Combine(store, "builder.md")), StringComparison.Ordinal);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(notes, "builder.md")));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task InitWithALinkedClaudeDirectoryOutsideTheRepoCountsAndListsTheRefusedFilesAndExitsTwoAsync()
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claude"), outside);
        string[] before = Snapshot(outside);

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string[] lines = Lines(result);
        int harnessLine = Array.FindIndex(lines, line => line.StartsWith("claude: ", StringComparison.Ordinal));
        Assert.True(harnessLine >= 0, "no 'claude:' line");
        Assert.Matches(RefusedCount(), lines[harnessLine]);
        Assert.Contains(lines[(harnessLine + 1)..], line => line.StartsWith("  ", StringComparison.Ordinal) && line.Contains(agentTail, StringComparison.Ordinal) && line.Contains(" → ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("AGENTS.md: ", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(outside));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task ALinkedVscodeDirectoryOutsideTheRepoRefusesItsMcpJsonAndCreatesNothingThereAsync()
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".vscode"), outside);
        string[] before = Snapshot(outside);

        CliResult result = await SyncAsync("--force");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string line = Assert.Single(Section(result, RefusedHeader));
        Assert.Contains(Path.Combine(".vscode", "mcp.json"), line, StringComparison.Ordinal);
        Assert.EndsWith($" → {Path.Combine(outside, "mcp.json")}", line, pathComparison);
        Assert.False(File.Exists(Path.Combine(outside, "mcp.json")));
        Assert.Equal(before, Snapshot(outside));
        AssertNoCrash(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task ALinkedClaustrumDirectoryOutsideTheRepoRefusesTheManifestAndCreatesNothingThereAsync(string flag)
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cli.Cwd, ".claustrum"), outside);
        string[] before = Snapshot(outside);

        CliResult result = await SyncAsync(Flags(flag));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string line = Assert.Single(Section(result, RefusedHeader));
        Assert.Contains(Path.Combine(".claustrum", "sync-manifest.json"), line, StringComparison.Ordinal);
        Assert.EndsWith($" → {Path.Combine(outside, "sync-manifest.json")}", line, pathComparison);
        Assert.False(File.Exists(Path.Combine(outside, "sync-manifest.json")));
        Assert.Equal(before, Snapshot(outside));
        AssertNoCrash(result);
    }

    [GeneratedRegex(@", \d+ refused \(outside the repo\)$")]
    private static partial Regex RefusedCount();
}
