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
        CancellationToken cancellationToken,
        IProcessLive? live = null)
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

        // AutoFlush on both, or the 4 KB FileStream buffer holds a quiet run's whole output until
        // exit and `last_line` (JobManager, MCP job_status) has nothing to report while it matters.
        await using StreamWriter stdoutLog = new(job.StdoutLog, append: false) { AutoFlush = true };
        await using StreamWriter stderrLog = new(job.StderrLog, append: false) { AutoFlush = true };

        Action<string>? guardedStreamLine = onStreamLine is null ? null : Guard(onStreamLine);
        Action<string>? guardedLiveLine = live is null ? null : Guard(live.OnStdoutLine);
        process.OutputDataReceived += (_, e) => Pump(e.Data, stdout, stdoutLog, guardedStreamLine, guardedLiveLine);
        process.ErrorDataReceived += (_, e) => Pump(e.Data, stderr, stderrLog, onStreamLine: null, onLiveLine: null);

        Stopwatch stopwatch = Stopwatch.StartNew();
        process.Start();

        using PausableDeadline deadline = new(timeout);
        live?.Attach(deadline);

        // Pumps started before stdin is written: a prompt larger than the pipe buffer would otherwise
        // deadlock against a child already blocked writing stdout nobody is draining yet.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using CancellationTokenSource liveStop = new();
        Task liveLoop = Task.CompletedTask;

        ProcessTermination termination = ProcessTermination.Completed;
        try
        {
            // Inside the timeout, not before it: a child that never drains its stdin would otherwise
            // block this write with nothing left to interrupt it. A live session keeps stdin open for
            // the conversation and closes it itself; it starts only after the first message is written,
            // so a human's early message cannot overtake the brief.
            await WriteStdinAsync(process, spec.StdinText, keepOpen: live is not null, linked.Token);
            if (live is not null)
                liveLoop = live.RunAsync(process.StandardInput, liveStop.Token);

            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            termination = cancellationToken.IsCancellationRequested ? ProcessTermination.Cancelled : ProcessTermination.TimedOut;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        finally
        {
            if (live is not null)
                await EndLiveAsync(live, liveStop, liveLoop);
        }

        // The terminal's signal can kill the child before our handler cancels the token: a cancel, not a failed backend.
        if (termination == ProcessTermination.Completed && SignalExit.IsCancelSignal(process.ExitCode) && await SignalExit.CancelArrivesAsync(cancellationToken))
            termination = ProcessTermination.Cancelled;

        stopwatch.Stop();
        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), termination, stopwatch.Elapsed);
    }

    // Stdin always ends up closed, and is never inherited: a child holding the MCP host's own live
    // JSON-RPC pipe can block on it (NOTES.md "MCP child stdin inheritance hung git"). cursor and claude
    // are fed their whole prompt first — `cursor-agent -p` has no prompt-file flag (issue #14), and a
    // claude brief on argv was visible to `pgrep -f` (#68) — so ProcessSpec.StdinText arrives here.
    // `keepOpen` (a live session, #124) is the one exception: the session closes it when it ends.
    private static async Task WriteStdinAsync(System.Diagnostics.Process process, string? stdinText, bool keepOpen, CancellationToken cancellationToken)
    {
        try
        {
            if (stdinText is { } text)
                await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);

            if (keepOpen)
                await process.StandardInput.FlushAsync(cancellationToken);
            else
                process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A child that exited before reading its prompt (a startup error: cursor's own
            // workspace-trust and plan-tier refusals both exit in ~1-5s) makes this a broken pipe.
            // Its exit code and stderr diagnose that far better than a throw from here would.
        }
    }

    // The callback runs on a thread-pool thread inside OutputDataReceived: an exception out of it
    // terminates the whole process. A formatter that chokes on one line must cost that line, not
    // the job; the first failure is said once on stderr, the rest are dropped.
    private static Action<string> Guard(Action<string> callback)
    {
        int reported = 0;
        return line =>
        {
            try
            {
                callback(line);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref reported, 1) != 0)
                    return;

                try
                {
                    Console.Error.WriteLine($"claustrum: stream callback failed ({ex.GetType().Name}: {ex.Message}); further failures are not reported");
                }
                catch (IOException)
                {
                    // stderr is gone as well; nothing left to tell.
                }
            }
        };
    }

    // The session stops when the process is gone (killed or exited), then hears about it: Ended sets live.json
    // to done and closes what is left of stdin. The loop is awaited so nothing of it runs on after the receipt.
    private static async Task EndLiveAsync(IProcessLive live, CancellationTokenSource liveStop, Task liveLoop)
    {
        await liveStop.CancelAsync();
        try
        {
            await liveLoop;
        }
        catch (OperationCanceledException)
        {
        }

        live.Ended();
    }

    private static void Pump(string? line, StringBuilder buffer, StreamWriter log, Action<string>? onStreamLine, Action<string>? onLiveLine)
    {
        if (line is null)
            return;

        buffer.AppendLine(line);
        log.WriteLine(line);
        onLiveLine?.Invoke(line);
        onStreamLine?.Invoke(line);
    }
}
