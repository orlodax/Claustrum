using System.Diagnostics;
using System.Globalization;
using System.Text;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The login-file half of `backends doctor <name> --probe`'s `auth:` line (BackendsCommands.
// LoginFileStatusFor), driven through the real built binary the way CliEndToEndTests exercises the
// rest of `doctor` — this is `--probe`, so the process-door shape (crash vs. a real answer, exit
// code) is exactly what an in-process test cannot see. COPILOT_HOME relocates the login file this
// code reads; PATH is stripped to a scratch, empty directory so the real, authenticated copilot
// 1.0.87 installed on this machine (AGENTS.md's own warning) can never be found, let alone spawned —
// no subprocess of a real backend runs here, paid or not. NOTES.md "The copilot backend, validated
// against a real install" box 7 holds the six shapes this pins.
public sealed class BackendsCommandsCopilotAuthTests : IDisposable
{
    private const int Ok = 0;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-copilot-auth-cwd-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-copilot-auth-home-").FullName;
    private readonly string copilotHome = Directory.CreateTempSubdirectory("claustrum-copilot-auth-copilothome-").FullName;
    private readonly string emptyPath = Directory.CreateTempSubdirectory("claustrum-copilot-auth-path-").FullName;

    public void Dispose()
    {
        TempTree.Delete(cwd);
        TempTree.Delete(home);
        TempTree.Delete(copilotHome);
        TempTree.Delete(emptyPath);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"loggedInUsers":[{"host":"https://github.com","login":"someone"}]}""", "present (logged in, {0} — no env var set)")]
    [InlineData(/*lang=json,strict*/ """{"loggedInUsers":[]}""", "not set (env var absent; {0} lists no logged-in user)")]
    [InlineData(/*lang=json,strict*/ "{}", "not set (env var absent; {0} lists no logged-in user)")]
    [InlineData(/*lang=json,strict*/ "[1,2,3]", "not set (env var absent; {0} present but unreadable)")]
    [InlineData(/*lang=json,strict*/ "\"hello\"", "not set (env var absent; {0} present but unreadable)")]
    [InlineData("not json", "not set (env var absent; {0} present but unreadable)")]
    public async Task ConfigJsonShapeProducesTheExpectedAuthLineAsync(string configJsonContent, string expectedTemplate)
    {
        string configPath = Path.Combine(copilotHome, "config.json");
        File.WriteAllText(configPath, configJsonContent);

        (int exitCode, string stdout, string stderr) = await RunDoctorProbeAsync("copilot");

        string expected = string.Format(CultureInfo.InvariantCulture, expectedTemplate, configPath);
        Assert.Equal(Ok, exitCode);
        Assert.Contains($"auth:    {expected}", stdout, StringComparison.Ordinal);
        Assert.Contains("merged config:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConfigJsonReportsNotPresentAsync()
    {
        string configPath = Path.Combine(copilotHome, "config.json");

        (int exitCode, string stdout, _) = await RunDoctorProbeAsync("copilot");

        Assert.Equal(Ok, exitCode);
        Assert.Contains($"auth:    not set (env var absent; {configPath} not present)", stdout, StringComparison.Ordinal);
        Assert.Contains("merged config:", stdout, StringComparison.Ordinal);
    }

    // The unchanged half of AuthStatusFor: every backend but copilot has no login file this code
    // reads, so it must keep saying so rather than guess.
    [Fact]
    public async Task NonCopilotBackendKeepsTheUncheckedMessageAsync()
    {
        (int exitCode, string stdout, _) = await RunDoctorProbeAsync("claude");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:    not set (env var absent; a login file may still work — not checked)", stdout, StringComparison.Ordinal);
        Assert.Contains("merged config:", stdout, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunDoctorProbeAsync(string backendName)
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
            // Null would mean the parent's own console code page, which on Windows decodes the
            // child's UTF-8 (ConsoleEncoding) into mojibake — both ends have to agree.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("backends");
        startInfo.ArgumentList.Add("doctor");
        startInfo.ArgumentList.Add(backendName);
        startInfo.ArgumentList.Add("--probe");
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        startInfo.Environment["COPILOT_HOME"] = copilotHome;
        startInfo.Environment["PATH"] = emptyPath;

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
