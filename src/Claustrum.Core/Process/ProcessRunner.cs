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
            // Without this a piped prompt (ProcessSpec.StdinText) would go out in Console.InputEncoding
            // — a legacy codepage on Windows; no BOM, or it would be prepended to the prompt itself.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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

        // Pumps started before stdin is written: a prompt larger than the pipe buffer would otherwise
        // deadlock against a child already blocked writing stdout nobody is draining yet.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        ProcessTermination termination = ProcessTermination.Completed;
        try
        {
            // Inside the timeout, not before it: a child that never drains its stdin would otherwise
            // block this write with nothing left to interrupt it.
            await WriteStdinAsync(process, spec.StdinText, linked.Token);
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

    // Stdin always ends up closed, and is never inherited: a child holding the MCP host's own live
    // JSON-RPC pipe can block on it (NOTES.md "MCP child stdin inheritance hung git"). cursor is the
    // only backend fed anything first — `cursor-agent -p` has no prompt-file flag and reads its whole
    // prompt from stdin (issue #14), so ProcessSpec.StdinText arrives here.
    private static async Task WriteStdinAsync(System.Diagnostics.Process process, string? stdinText, CancellationToken cancellationToken)
    {
        try
        {
            if (stdinText is { } text)
                await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A child that exited before reading its prompt (a startup error: cursor's own
            // workspace-trust and plan-tier refusals both exit in ~1-5s) makes this a broken pipe.
            // Its exit code and stderr diagnose that far better than a throw from here would.
        }
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
