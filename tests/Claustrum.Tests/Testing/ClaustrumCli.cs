using System.Diagnostics;

namespace Claustrum.Tests.Testing;

public sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The real built `claustrum` binary run in a throwaway working directory, for the tests that need to
/// see an exit code, a stream or a pipe — what no in-process call observes. One instance owns one cwd
/// and one CLAUSTRUM_HOME; HOME/APPDATA point at that home too, so a developer's own user-level
/// `claustrum.json` can never change what a test resolves. A run that outlives its timeout is killed
/// rather than left behind.
/// </summary>
public sealed class ClaustrumCli : IDisposable
{
    private static readonly TimeSpan timeout = TimeSpan.FromSeconds(60);

    public string Cwd { get; } = Directory.CreateTempSubdirectory("claustrum-cli-").FullName;

    public string Home { get; } = Directory.CreateTempSubdirectory("claustrum-cli-home-").FullName;

    /// <summary>Where a job would be minted; absent until some run creates one.</summary>
    public string JobsDirectory => Path.Combine(Home, "jobs");

    public string CastsDirectory => Path.Combine(Cwd, ".claustrum", "casts");

    public void Dispose()
    {
        TempTree.Delete(Cwd);
        TempTree.Delete(Home);
    }

    /// <summary>
    /// A `.git` entry is all Config.Load looks for (GitRootLocator) before it reads `claustrum.json`,
    /// so this makes <see cref="Cwd"/> a config root without spawning git.
    /// </summary>
    public void MarkAsGitRoot() => Directory.CreateDirectory(Path.Combine(Cwd, ".git"));

    /// <param name="args">The command line, one element per argument.</param>
    /// <param name="stdin">Written to the child's standard input, which is then closed.</param>
    /// <param name="pathOverride">
    /// Replaces PATH for the child; an empty directory means no backend binary can be found on this
    /// machine, installed or not.
    /// </param>
    public async Task<CliResult> RunAsync(string[] args, string stdin, string? pathOverride)
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = Cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment["CLAUSTRUM_HOME"] = Home;
        startInfo.Environment["HOME"] = Home;
        startInfo.Environment["APPDATA"] = Home;

        // The real paid probe is a house rule violation waiting to happen on a machine with a backend
        // logged in (CliEndToEndTests' own comment): every spawn here skips it.
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        if (pathOverride is not null)
            startInfo.Environment["PATH"] = pathOverride;

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("claustrum failed to start");
        try
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited before reading its input — its exit code and streams still answer.
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }
}
