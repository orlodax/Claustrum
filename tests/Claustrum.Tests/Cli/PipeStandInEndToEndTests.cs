using Claustrum.Cli;
using Claustrum.Core.IO;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// 2026-10-13 through the built binary: a FIFO standing where a file the command reads should be is refused with exit 2 and the
// reason, never opened (which blocks), and the rest of the command still runs. ClaustrumCli kills a run that outlives its
// timeout, so a regression is a failed test rather than a hung one. Unix only (mkfifo).
public sealed class PipeStandInEndToEndTests : IDisposable
{
    private const string RefusedHeader = "refused (sync never writes through a link that leaves the repo, nor into a pipe or socket):";

    private readonly ClaustrumCli cli = new();

    public void Dispose() => cli.Dispose();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string Agent(string name) => Path.Combine(cli.Cwd, ".claude", "agents", $"{name}.md");

    private void FifoAgent(string name)
    {
        Directory.CreateDirectory(Path.Combine(cli.Cwd, ".claude", "agents"));
        PipeFiles.MakeFifo(Agent(name));
    }

    private Task<CliResult> SyncAsync(params string[] args) => cli.RunAsync(["sync", "--only", "claude", "--roles", "builder,tester", .. args], "", null);

    [Fact]
    public async Task SyncWithAFifoAgentRefusesItListsItUnderTheHeaderAndStillWritesTheOthersAsync()
    {
        FifoAgent("builder");

        CliResult result = await SyncAsync();

        string stdout = Lf(result.Stdout);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(RefusedHeader, stdout, StringComparison.Ordinal);
        string[] lines = stdout.Split('\n');
        string line = lines[(Array.IndexOf(lines, RefusedHeader) + 1)..].First(l => l.StartsWith("  ", StringComparison.Ordinal));
        Assert.Contains("builder.md", line, StringComparison.Ordinal);
        Assert.Contains("not a regular file", line, StringComparison.Ordinal);
        Assert.True(File.Exists(Agent("tester")), "the other agent should have been written");
        Assert.StartsWith("---", File.ReadAllText(Agent("tester")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    public async Task DryRunAndCheckExitTwoOnAFifoAgentAsync(string flag)
    {
        FifoAgent("builder");

        CliResult result = await SyncAsync(flag);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(RefusedHeader, Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(File.Exists(Agent("tester")), "a dry run or check writes nothing");
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData("AGENTS.md")]
    public async Task InitWithAFifoGitignoreOrAgentsMdRefusesThatFileAndFinishesTheRestAsync(string name)
    {
        string path = Path.Combine(cli.Cwd, name);
        PipeFiles.MakeFifo(path);

        CliResult result = await cli.RunAsync(["init"], "", null);

        string stdout = Lf(result.Stdout);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains($"{name}: refused (", stdout, StringComparison.Ordinal);
        Assert.Contains("not a regular file", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cli.Cwd, "claustrum.json")), "the config step completes");
        Assert.True(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum", "casts")), "the scaffold step completes");
        Assert.NotNull(StillAPipe(path));
    }

    // Still a pipe afterwards: a regular file would have been replaced in its place.
    private static string? StillAPipe(string path) =>
        PlainFile.TryReadAllText(path, out _, out string? refusal) ? null : refusal;

    [Fact]
    public async Task CastShowOfAFifoCastExitsTwoNamedAsNotARegularFileAsync()
    {
        Directory.CreateDirectory(cli.CastsDirectory);
        PipeFiles.MakeFifo(Path.Combine(cli.CastsDirectory, "mine.json"));

        CliResult result = await cli.RunAsync(["cast", "show", "mine"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFifoClaustrumJsonIsAConfigErrorNotAHangAsync()
    {
        cli.MarkAsGitRoot();
        PipeFiles.MakeFifo(Path.Combine(cli.Cwd, "claustrum.json"));

        CliResult result = await cli.RunAsync(["cast", "questions"], "", null);

        Assert.NotEqual(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
    }

    // Regression: the pipe guard must not refuse a brief that really arrives through a pipe, which is how stdin is spelled.
    [Fact]
    public async Task ABriefFileThatIsDevStdinIsStillReadAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/stdin is a Unix path.");

        cli.MarkAsGitRoot();
        string empty = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

        CliResult result = await cli.RunAsync(["run", "builder", "--brief-file", "/dev/stdin", "--json"], "do the thing\n", empty);

        // No backend is installed, so the run fails afterwards; what matters is that the brief was read (an empty one is a usage error about the brief).
        Assert.DoesNotContain("no brief", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not a regular file", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }
}
