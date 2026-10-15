using System.Runtime.Versioning;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #112 through the built binary: a claustrum.json the process may not read (mode 000) is a named refusal with exit 2, never an
// unhandled error. In a directory with no git root nothing loads it, so `init` meets it when it asks whether the file is there
// and prints `claustrum.json: refused (cannot be read: …)` while every other step still runs; at a git root Config.Load meets it
// first and the command fails with the same words on stderr. POSIX modes only; skipped for an account a mode does not stop (root).
public sealed class InitUnreadableConfigEndToEndTests : IDisposable
{
    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private string? locked;

    public void Dispose()
    {
        if (locked is not null && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        cli.Dispose();
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [UnsupportedOSPlatform("windows")]
    private string LockedClaustrumJson()
    {
        string path = Path.Combine(cli.Cwd, "claustrum.json");
        File.WriteAllText(path, /*lang=json,strict*/ """{"models":{}}""");
        File.SetUnixFileMode(path, UnixFileMode.None);
        locked = path;
        try
        {
            using FileStream stream = File.OpenRead(path);
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }

        Assert.Skip("this account reads a mode-000 file (root, or a capability that bypasses modes).");
        return path;
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task InitWithAnUnreadableClaustrumJsonAndNoGitRootRefusesItExitingTwoAndFinishesTheRestAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        string path = LockedClaustrumJson();

        CliResult result = await cli.RunAsync(["init"], "", null);

        string stdout = Lf(result.Stdout);
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("claustrum.json: refused (cannot be read:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("claustrum.json: written", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("already present", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cli.Cwd, ".gitignore")), "the .gitignore step still runs");
        Assert.True(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum", "casts")), "the scaffold step still runs");
        Assert.Contains(stdout.Split('\n'), line => line.StartsWith("claude: ", StringComparison.Ordinal));
        Assert.Contains(stdout.Split('\n'), line => line.StartsWith("AGENTS.md: ", StringComparison.Ordinal));
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task InitAtAGitRootWithAnUnreadableClaustrumJsonExitsTwoNamingTheFileOnStderrAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        cli.MarkAsGitRoot();
        _ = LockedClaustrumJson();

        CliResult result = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("claustrum.json", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("cannot be read", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum")), "nothing is created before the config is loaded");
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("cast questions")]
    [UnsupportedOSPlatform("windows")]
    public async Task OtherCommandsAtAGitRootNameAnUnreadableClaustrumJsonTooAsync(string verb)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        cli.MarkAsGitRoot();
        _ = LockedClaustrumJson();

        CliResult result = await cli.RunAsync(verb.Split(' '), "", null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cannot be read", result.Stdout + result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
    }
}
