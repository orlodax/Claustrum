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

    // ProcessSpec.StdinText (issue #14, NOTES.md "The cursor backend, validated against a real
    // install"): cursor-agent -p reads its whole prompt from stdin when argv carries none.
    [Fact]
    public async Task StdinTextIsWrittenAndTheChildSeesEofAsync()
    {
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = ReadStdinCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], [], StdinText: "the rendered prompt");
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(ProcessTermination.Completed, outcome.Termination);
            Assert.Contains("the rendered prompt", outcome.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    // NOTES.md: "a prompt past the pipe buffer would otherwise deadlock against a child blocked on
    // an undrained stdout" — this child never reads stdin at all, so the write itself has to be the
    // thing the run's timeout cancels, not WaitForExitAsync.
    [Fact]
    public async Task APromptLargerThanThePipeBufferEndsAtTheTimeoutInsteadOfHangingAsync()
    {
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = SleepCommand(30);
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], [], StdinText: new string('x', 256 * 1024));
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(3), CancellationToken.None);

            Assert.Equal(ProcessTermination.TimedOut, outcome.Termination);
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task AUtf8PromptArrivesByteExactWithNoBomAsync()
    {
        JobPaths job = CreateTempJob();
        try
        {
            const string prompt = "héllo — ⚙ tëst";
            (string exe, string[] args) = ReadStdinCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], [], StdinText: prompt);
            ProcessRunner runner = new(new RealPlatform());

            ProcessOutcome outcome = await runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Equal(ProcessTermination.Completed, outcome.Termination);
            Assert.Contains(prompt, outcome.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain('﻿', outcome.Stdout);
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    // ProcessRunner.stdoutLog is opened with AutoFlush = true (NOTES.md/the field's own comment)
    // specifically so JobManager.GetStatus's "last output line" has something to report while a job is
    // still running — this proves the log file actually grows mid-run rather than only appearing whole
    // at exit. Polls for the first line with a bounded wait instead of a fixed sleep, so the assertion
    // is deterministic without being slow on a fast machine or flaky on a loaded one.
    [Fact]
    public async Task StdoutLogGrowsWhileTheProcessIsStillRunningAsync()
    {
        JobPaths job = CreateTempJob();
        try
        {
            (string exe, string[] args) = GrowingOutputCommand();
            ProcessSpec spec = new(exe, args, Path.GetTempPath(), [], []);
            ProcessRunner runner = new(new RealPlatform());

            Task<ProcessOutcome> run = runner.RunAsync(spec, backendConfig: null, job, envPassthroughAll: false, onStreamLine: null, TimeSpan.FromSeconds(30), CancellationToken.None);

            string firstLine = await PollForFirstLineAsync(job.StdoutLog, TimeSpan.FromSeconds(10));
            Assert.Equal("a", firstLine);
            // The process is still asleep (it prints "b" only after a 1s sleep): the log has grown to
            // exactly its first line, proof this was read mid-run and not after the process exited.
            Assert.False(run.IsCompleted, "the process finished before the mid-run read — the test proves nothing");

            ProcessOutcome outcome = await run;
            Assert.Equal(0, outcome.ExitCode);
            Assert.Contains("b", await File.ReadAllTextAsync(job.StdoutLog, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(job.Directory, recursive: true);
        }
    }

    private static async Task<string> PollForFirstLineAsync(string logPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                string[] lines = await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken);
                if (lines.Length > 0)
                    return lines[0];
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"'{logPath}' never gained a first line within {timeout}");
    }

    private static (string Exe, string[] Args) GrowingOutputCommand() => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "echo a & ping -n 2 127.0.0.1 >nul & echo b"])
        : ("sh", ["-c", "echo a; sleep 1; echo b"]);

    private static (string Exe, string[] Args) SleepCommand(int seconds) => OperatingSystem.IsWindows()
        ? ("cmd", ["/c", "ping", "-n", (seconds + 1).ToString(), "127.0.0.1"])
        : ("sh", ["-c", $"sleep {seconds}"]);

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
