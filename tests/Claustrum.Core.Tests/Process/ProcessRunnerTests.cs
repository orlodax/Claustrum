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

    // Finding #11's fix is two lines that have to stay together: RedirectStandardInput = true *and*
    // StandardInput.Close(). Redirecting without closing leaves the child on a pipe nobody ever
    // writes to, so a child that reads stdin blocks until the run's own timeout — which is what this
    // catches, on either OS. (Dropping both lines instead is only observable against a real host
    // with a live stdin pipe: see Claustrum.Tests' McpStdioServerTests, and note the measurement
    // there that the resulting hang reproduces on Windows but not on Linux.)
    [Fact]
    public async Task AChildThatReadsStdinSeesEofInsteadOfBlockingAsync()
    {
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = ReadStdinCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], []);
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(ProcessTermination.Completed, outcome.Termination);
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    private static (string Exe, string[] Args) ReadStdinCommand() => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "more"])
        : ("sh", ["-c", "cat"]);

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
