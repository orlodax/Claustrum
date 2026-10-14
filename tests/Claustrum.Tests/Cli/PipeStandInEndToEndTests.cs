using Claustrum.Cli;
using Claustrum.Core.IO;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// 2026-10-13 through the built binary: a FIFO standing where a file the command reads should be is refused with exit 2 and the
// reason, never opened (which blocks), and the rest of the command still runs. ClaustrumCli kills a run that outlives its
// timeout, so a regression is a failed test rather than a hung one. Unix only (mkfifo).
public sealed class PipeStandInEndToEndTests : IDisposable
{
    private const string RefusedHeader = "refused (sync never writes through a link that leaves the repo, nor into a pipe, socket or device):";

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

    // ---- writes (2026-10-14) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CastUseOntoAFifoDefaultExitsTwoWithoutHangingAsync()
    {
        Directory.CreateDirectory(cli.CastsDirectory);
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "mine.json"), CastLinkFixture.ValidCast);
        PipeFiles.MakeFifo(Path.Combine(cli.CastsDirectory, "default.json"));

        CliResult result = await cli.RunAsync(["cast", "use", "mine"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastCreateOntoAFifoDefaultExitsTwoWithoutHangingAsync()
    {
        Directory.CreateDirectory(cli.CastsDirectory);
        PipeFiles.MakeFifo(Path.Combine(cli.CastsDirectory, "default.json"));
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus"}""");

        CliResult result = await cli.RunAsync(["cast", "create", "--answers", "answers.json"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitWithAFifoMcpJsonRefusesItWithTheNewSummaryNoteAndFinishesTheRestAsync()
    {
        PipeFiles.MakeFifo(Path.Combine(cli.Cwd, ".mcp.json"));

        CliResult result = await cli.RunAsync(["init"], "", null);

        string stdout = Lf(result.Stdout);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("refused (outside the repo, or a pipe, socket or device)", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cli.Cwd, "claustrum.json")));
        Assert.NotNull(StillAPipe(Path.Combine(cli.Cwd, ".mcp.json")));
    }

    [Fact]
    public async Task InitWithAFifoClaustrumJsonAndNoGitRootRefusesItExitingTwoAndTheFifoStaysAsync()
    {
        string path = Path.Combine(cli.Cwd, "claustrum.json");
        PipeFiles.MakeFifo(path);

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("claustrum.json: refused (", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Contains("not a regular file", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cli.Cwd, ".gitignore")), "the other steps still complete");
        Assert.NotNull(StillAPipe(path));
    }

    [Fact]
    public async Task InitWithARegularClaustrumJsonStillSaysAlreadyPresentAsync()
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{}}""");

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("claustrum.json: already present, left unchanged", Lf(result.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitWithADirectoryNamedGitignoreRefusesItExitingTwoAsync()
    {
        Directory.CreateDirectory(Path.Combine(cli.Cwd, ".gitignore"));

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(".gitignore: refused (", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(cli.Cwd, ".gitignore")));
        Assert.DoesNotContain("Unhandled", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitInARepositoryWithAFifoClaustrumJsonExitsTwoAndTheFifoStaysAsync()
    {
        cli.MarkAsGitRoot();
        string path = Path.Combine(cli.Cwd, "claustrum.json");
        PipeFiles.MakeFifo(path);

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", Lf(result.Stdout) + Lf(result.Stderr), StringComparison.Ordinal);
        Assert.NotNull(StillAPipe(path));
    }

    [Fact]
    public async Task AClaustrumJsonLinkedToDevZeroIsAConfigErrorAndSyncExitsTwoWithoutExhaustingMemoryAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("/dev/zero is a Unix device.");

        cli.MarkAsGitRoot();
        TestLinks.Create(Path.Combine(cli.Cwd, "claustrum.json"), "/dev/zero");

        CliResult result = await cli.RunAsync(["sync"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
    }

    // ---- doctor ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(".mcp.json")]
    [InlineData(".vscode/mcp.json")]
    [InlineData(".cursor/mcp.json")]
    [InlineData("opencode.json")]
    [InlineData("opencode.jsonc")]
    public async Task DoctorReportsAFifoRegistrationFileAsPresentNotARegularFileWithoutHangingAsync(string relative)
    {
        string path = Path.Combine([cli.Cwd, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? cli.Cwd);
        PipeFiles.MakeFifo(path);

        CliResult result = await cli.RunAsync(["backends", "doctor", "--probe"], "", null);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("present, not a regular file", Lf(result.Stdout), StringComparison.Ordinal);
    }

    // ---- --file still reads a pipe -------------------------------------------------------------------------------------

    [Fact]
    public async Task RunFileThatIsAPipeIsStillAttachedAsync()
    {
        FakeClaude.RequirePosixShell();
        using IsolatedRepo repo = new();
        repo.Script(new FakeClaudeScript { StdinLog = repo.StdinLog });
        string fifo = Path.Combine(repo.Repo, "attach.fifo");
        PipeFiles.MakeFifo(fifo);

        Task<CliResult> run = repo.RunAsync("run", "builder", "--brief", "hi", "--json", "--file", fifo);
        await using (FileStream writer = new(fifo, FileMode.Open, FileAccess.Write))
            await writer.WriteAsync("PIPED-ATTACHMENT\n"u8.ToArray(), TestContext.Current.CancellationToken);

        CliResult result = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("PIPED-ATTACHMENT", File.ReadAllText(repo.StdinLog), StringComparison.Ordinal);
    }
}
