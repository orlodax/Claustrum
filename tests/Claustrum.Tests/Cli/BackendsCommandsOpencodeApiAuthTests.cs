using System.Diagnostics;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The env-var and login-file halves of `backends doctor <name> --probe`'s `auth:` line
// (BackendsCommands.AuthStatusFor/LoginFileStatusFor) for opencode and api, driven through the real
// built binary the same way BackendsCommandsCopilotAuthTests exercises copilot's: --probe with
// CLAUSTRUM_SKIP_PROBE=1 never spawns anything, so this is a pure env/filesystem check, but only a
// subprocess test observes the crash-vs-real-answer shape the code path actually commits to. PATH is
// stripped to an empty scratch directory so the real opencode 2.0.12 (or curl) this machine has
// installed (AGENTS.md's own warning) can never be found, let alone spawned. HOME/XDG_DATA_HOME are
// relocated so the login-file check never touches the real one, and every relevant key is explicitly
// set or stripped for the child so no result depends on the caller's own shell — this sandbox's own
// OPENROUTER_API_KEY (AGENTS.md) included.
public sealed class BackendsCommandsOpencodeApiAuthTests : IDisposable
{
    private const int Ok = 0;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-oc-api-auth-cwd-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-oc-api-auth-home-").FullName;
    private readonly string xdgDataHome = Directory.CreateTempSubdirectory("claustrum-oc-api-auth-xdg-").FullName;
    private readonly string emptyPath = Directory.CreateTempSubdirectory("claustrum-oc-api-auth-path-").FullName;

    private static readonly string[] opencodeEnvVars = ["ANTHROPIC_API_KEY", "OPENROUTER_API_KEY", "OPENCODE_API_KEY"];
    private static readonly string[] apiEnvVars = ["OPENROUTER_API_KEY", "ANTHROPIC_API_KEY"];

    public void Dispose()
    {
        TempTree.Delete(cwd);
        TempTree.Delete(home);
        TempTree.Delete(xdgDataHome);
        TempTree.Delete(emptyPath);
    }

    [Fact]
    public async Task OpencodeWithNoEnvVarAndNoLoginFileReportsTheMeasuredNegativeAsync()
    {
        string legacyAuth = Path.Combine(xdgDataHome, "opencode", "auth.json");

        (int exitCode, string stdout, string stderr) = await RunDoctorProbeAsync("opencode", opencodeEnvVars);

        Assert.Equal(Ok, exitCode);
        Assert.Contains($"auth:    not set (env var absent; {legacyAuth} not present, and 2.x keeps credentials in opencode.db, which this does not read)", stdout, StringComparison.Ordinal);
        Assert.Contains("merged config:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeWithALoginFilePresentReportsItAsync()
    {
        string opencodeDir = Path.Combine(xdgDataHome, "opencode");
        Directory.CreateDirectory(opencodeDir);
        string legacyAuth = Path.Combine(opencodeDir, "auth.json");
        File.WriteAllText(legacyAuth, "{}");

        (int exitCode, string stdout, _) = await RunDoctorProbeAsync("opencode", opencodeEnvVars);

        Assert.Equal(Ok, exitCode);
        Assert.Contains($"auth:    present (login file at {legacyAuth} — no env var set; contents not read)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpencodeWithAnyRelevantEnvVarSetReportsPresentWithoutCheckingTheLoginFileAsync()
    {
        (int exitCode, string stdout, _) = await RunDoctorProbeAsync(
            "opencode", ["ANTHROPIC_API_KEY", "OPENCODE_API_KEY"], setKeys: new() { ["OPENROUTER_API_KEY"] = "sk-or-test" });

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:    present (env var set — a login file may also work even if not)", stdout, StringComparison.Ordinal);
    }

    // The child's own inherited OPENROUTER_API_KEY (set for the real backend elsewhere in this
    // sandbox) must be stripped, not merely left unset in this test process — a subprocess inherits
    // the parent's environment by default, so leaving it in place would assert nothing.
    [Fact]
    public async Task ApiWithNeitherKeySetForTheChildReportsTheMeasuredNegativeAsync()
    {
        (int exitCode, string stdout, string stderr) = await RunDoctorProbeAsync("api", apiEnvVars);

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:    not set (env var absent; this backend has no login file — it is a direct HTTPS call)", stdout, StringComparison.Ordinal);
        Assert.Contains("merged config:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiWithOpenrouterKeySetReportsPresentAsync()
    {
        (int exitCode, string stdout, _) = await RunDoctorProbeAsync(
            "api", ["ANTHROPIC_API_KEY"], setKeys: new() { ["OPENROUTER_API_KEY"] = "sk-or-test" });

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:    present (env var set — a login file may also work even if not)", stdout, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunDoctorProbeAsync(string backendName, string[] stripKeys, Dictionary<string, string>? setKeys = null)
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
        };
        startInfo.ArgumentList.Add("backends");
        startInfo.ArgumentList.Add("doctor");
        startInfo.ArgumentList.Add(backendName);
        startInfo.ArgumentList.Add("--probe");
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
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
