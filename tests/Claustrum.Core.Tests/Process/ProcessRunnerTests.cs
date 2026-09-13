using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Tests.Process;

// Spawns a real trivial process (docs brief item 3's fifth bullet) to prove EnvAllowList is
// actually wired into the child's environment, not just unit-tested in isolation — NOTES.md "Env
// allow-list is dead without clearing ProcessStartInfo.Environment" was exactly this kind of bug.
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task FilteredEnvVarNeverReachesTheChildButInjectedEnvDoesAsync()
    {
        string marker = "CLAUSTRUM_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(marker, "leak-me-not");
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = DumpEnvCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), new Dictionary<string, string> { ["CLAUSTRUM_TEST_INJECTED"] = "hello" }, []);
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(0, outcome.ExitCode);
            Assert.DoesNotContain(marker, outcome.Stdout, StringComparison.Ordinal);
            Assert.Contains("CLAUSTRUM_TEST_INJECTED", outcome.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task PassthroughAllLetsTheFilteredVarReachTheChildAsync()
    {
        string marker = "CLAUSTRUM_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(marker, "now-visible");
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = DumpEnvCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], []);
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: true, onStreamLine: null, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Contains(marker, outcome.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    private static (string Exe, string[] Args) DumpEnvCommand() => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "set"])
        : ("sh", ["-c", "env"]);

    private static JobPaths CreateTempJob()
    {
        string dir = Directory.CreateTempSubdirectory("claustrum-job-").FullName;
        return new JobPaths("test-job", dir, Path.Combine(dir, "request.json"), Path.Combine(dir, "system.md"),
            Path.Combine(dir, "stdout.log"), Path.Combine(dir, "stderr.log"), Path.Combine(dir, "result.json"));
    }
}
