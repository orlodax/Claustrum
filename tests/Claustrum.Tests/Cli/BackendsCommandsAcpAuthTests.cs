using System.Diagnostics;
using System.Text;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #130: `acp-opencode` logs in as opencode does, so `backends doctor acp-opencode --probe` prints opencode's `auth:` line (BackendsCommands passes the backend's
// harness to AuthStatusFor). Driven through the real built binary exactly as BackendsCommandsOpencodeApiAuthTests drives opencode's: --probe with
// CLAUSTRUM_SKIP_PROBE=1 spawns nothing, PATH is an empty scratch directory so no real opencode can be found, let alone run, and HOME/XDG_DATA_HOME and
// every relevant key are set or stripped for the child so no result depends on the caller's shell.
public sealed class BackendsCommandsAcpAuthTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-acp-auth-cwd-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-acp-auth-home-").FullName;
    private readonly string xdgDataHome = Directory.CreateTempSubdirectory("claustrum-acp-auth-xdg-").FullName;
    private readonly string emptyPath = Directory.CreateTempSubdirectory("claustrum-acp-auth-path-").FullName;

    private static readonly string[] keys = ["ANTHROPIC_API_KEY", "OPENROUTER_API_KEY", "OPENCODE_API_KEY"];

    public void Dispose()
    {
        TempTree.Delete(cwd);
        TempTree.Delete(home);
        TempTree.Delete(xdgDataHome);
        TempTree.Delete(emptyPath);
    }

    private string LegacyAuth => Path.Combine(xdgDataHome, "opencode", "auth.json");

    [Fact]
    public async Task WithNoEnvVarAndNoLoginFileItReportsOpencodesMeasuredNegativeAsync()
    {
        (int exitCode, string stdout, string stderr) = await RunAsync(["backends", "doctor", "acp-opencode", "--probe"], keys);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains($"auth:    not set (env var absent; {LegacyAuth} not present, and 2.x keeps credentials in opencode.db, which this does not read)", stdout, StringComparison.Ordinal);
        Assert.Contains("acp-opencode:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithOpencodesLoginFilePresentItReportsItAsync()
    {
        Directory.CreateDirectory(Path.Combine(xdgDataHome, "opencode"));
        File.WriteAllText(LegacyAuth, "{}");

        (int exitCode, string stdout, _) = await RunAsync(["backends", "doctor", "acp-opencode", "--probe"], keys);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains($"auth:    present (login file at {LegacyAuth} — no env var set; contents not read)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAnOpencodeEnvVarSetItReportsPresentWithoutLookingAtTheLoginFileAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync(["backends", "doctor", "acp-opencode", "--probe"], ["ANTHROPIC_API_KEY", "OPENCODE_API_KEY"], new() { ["OPENROUTER_API_KEY"] = "sk-or-test" });

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains("auth:    present (env var set — a login file may also work even if not)", stdout, StringComparison.Ordinal);
    }

    // Both backends in one run, same machine: the two auth lines are the same line.
    [Fact]
    public async Task TheAuthLineOfAcpOpencodeIsTheOneOfOpencodeInTheSameRunAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync(["backends", "doctor", "--probe"], keys);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Equal(AuthLineOf(stdout, "opencode"), AuthLineOf(stdout, "acp-opencode"));
        Assert.Contains("not set", AuthLineOf(stdout, "acp-opencode"), StringComparison.Ordinal);
    }

    // The harness decides the auth, not the backend's name: a name no auth rule knows would say so, and this one is not that.
    [Fact]
    public async Task TheAcpBackendsAuthLineIsNeverTheUnknownBackendFallbackAsync()
    {
        (_, string stdout, _) = await RunAsync(["backends", "doctor", "acp-opencode", "--probe"], keys);

        string line = AuthLineOf(stdout, "acp-opencode");
        Assert.DoesNotContain("acp-opencode", line, StringComparison.Ordinal);
        Assert.StartsWith("auth:", line.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendsListEndsWithTheOptInBackendAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync(["backends", "list"], keys);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Equal(["claude", "api", "opencode", "copilot", "cursor", "acp-opencode"], stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public async Task APrefixOfItsNameIsNoBackendAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync(["backends", "doctor", "acp"], keys);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("unknown backend 'acp'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoOpencodeOnPathItIsReportedNotFoundWithTheUsualProblemAndNoAdvisoryAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync(["backends", "doctor", "acp-opencode"], keys);

        Assert.Equal(ExitCodes.Ok, exitCode);
        Assert.Contains("found:   False", stdout, StringComparison.Ordinal);
        Assert.Contains("problem: 'opencode' was not found on PATH", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("advisory:", stdout, StringComparison.Ordinal);
    }

    /// <summary>The `auth:` line printed under <paramref name="backend"/>'s own heading in a doctor listing.</summary>
    private static string AuthLineOf(string stdout, string backend)
    {
        string[] lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int heading = Array.IndexOf(lines, $"{backend}:");
        Assert.True(heading >= 0, $"no section for {backend}");
        return lines.Skip(heading + 1).TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal)).Single(line => line.TrimStart().StartsWith("auth:", StringComparison.Ordinal)).Trim();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args, string[] stripKeys, Dictionary<string, string>? setKeys = null)
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        startInfo.Environment.Remove("CLAUSTRUM_PARENT_JOB"); // #66
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["XDG_DATA_HOME"] = xdgDataHome;
        startInfo.Environment["PATH"] = emptyPath;
        foreach (string key in stripKeys)
            startInfo.Environment[key] = null;
        foreach ((string key, string value) in setKeys ?? [])
            startInfo.Environment[key] = value;

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
