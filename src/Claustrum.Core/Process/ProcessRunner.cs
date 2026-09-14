using System.Diagnostics;
using System.Text;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

// ArgumentList only, never a joined string (docs/PLAN.md A4); stdout/stderr are pumped line-wise
// to the job's log files as they arrive, and `onStreamLine` (wired to Claustrum's own stderr by
// the caller for `--stream`) sees stdout lines only — the result JSON is stdout-only, after exit.
public sealed class ProcessRunner(IPlatform platform)
{
    public async Task<ProcessOutcome> RunAsync(
        ProcessSpec spec,
        BackendConfig? backendConfig,
        JobPaths job,
        bool envPassthroughAll,
        Action<string>? onStreamLine,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ResolvedBinary binary = BinaryLocator.Locate(spec.Exe, spec.Args, backendConfig, platform);

        ProcessStartInfo startInfo = new()
        {
            FileName = binary.Executable,
            WorkingDirectory = spec.Cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        foreach (string arg in binary.Args)
            startInfo.ArgumentList.Add(arg);

        // .NET pre-populates ProcessStartInfo.Environment with this process's own environment when
        // UseShellExecute is false, so without Clear() the allow-list below was a no-op and every
        // Claustrum-process variable leaked into the child regardless (NOTES.md "Env allow-list is
        // dead").
        startInfo.Environment.Clear();
        foreach (KeyValuePair<string, string> entry in EnvAllowList.Build(platform, spec.Env, envPassthroughAll))
            startInfo.Environment[entry.Key] = entry.Value;

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        StringBuilder stdout = new();
        StringBuilder stderr = new();

        await using StreamWriter stdoutLog = new(job.StdoutLog, append: false);
        await using StreamWriter stderrLog = new(job.StderrLog, append: false);

        process.OutputDataReceived += (_, e) => Pump(e.Data, stdout, stdoutLog, onStreamLine);
        process.ErrorDataReceived += (_, e) => Pump(e.Data, stderr, stderrLog, onStreamLine: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        process.Start();

        // Closed immediately, never fed: every backend takes its brief as an argv element (NOTES.md
        // "MCP child stdin inheritance hung git"), so this cannot truncate one — and without it the
        // child would inherit the MCP host's own live JSON-RPC pipe.
        process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        ProcessTermination termination = ProcessTermination.Completed;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            termination = cancellationToken.IsCancellationRequested ? ProcessTermination.Cancelled : ProcessTermination.TimedOut;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        stopwatch.Stop();
        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), termination, stopwatch.Elapsed);
    }

    private static void Pump(string? line, StringBuilder buffer, StreamWriter log, Action<string>? onStreamLine)
    {
        if (line is null)
            return;

        buffer.AppendLine(line);
        log.WriteLine(line);
        onStreamLine?.Invoke(line);
    }
}
