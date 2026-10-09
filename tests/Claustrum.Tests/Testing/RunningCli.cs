using System.Diagnostics;
using System.Text;

namespace Claustrum.Tests.Testing;

/// <summary>
/// The built `claustrum` binary started through <see cref="Process"/> and left running, for the tests that signal it.
/// Started here and not through a shell `&amp;`: a non-interactive shell starts background jobs with SIGINT ignored and
/// .NET keeps an inherited ignore, so `kill -INT` would do nothing (NOTES.md "Ctrl-C reaches the runner").
/// </summary>
public sealed class RunningCli : IDisposable
{
    private readonly Process process;
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly Lock gate = new();
    private readonly TaskCompletionSource<bool> stdoutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunningCli(string binary, string workingDirectory, string home, string path, IEnumerable<string> args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["APPDATA"] = home;
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        startInfo.Environment.Remove("CLAUSTRUM_PARENT_JOB"); // #66
        startInfo.Environment["PATH"] = path;

        process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Append(stdout, stdoutClosed, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, stderrClosed, e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public string Stdout
    {
        get
        {
            lock (gate)
                return stdout.ToString();
        }
    }

    public string Stderr
    {
        get
        {
            lock (gate)
                return stderr.ToString();
        }
    }

    /// <summary>
    /// True when the child started with SIGINT ignored, so a signal would do nothing: the test host itself was launched
    /// from a background job of a non-interactive shell. Linux only (`SigIgn` in /proc); elsewhere it is not checked.
    /// </summary>
    public bool IgnoresSigint()
    {
        string status = $"/proc/{process.Id}/status";
        if (!File.Exists(status))
            return false;

        string? line = File.ReadLines(status).FirstOrDefault(l => l.StartsWith("SigIgn:", StringComparison.Ordinal));
        return line is not null && (Convert.ToUInt64(line["SigIgn:".Length..].Trim(), 16) & (1UL << (2 - 1))) != 0;
    }

    /// <summary>`kill -INT` through the shell builtin: what a terminal's Ctrl-C delivers to the process.</summary>
    public async Task SendSigintAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new() { FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"kill -INT {process.Id}");
        using Process kill = Process.Start(startInfo) ?? throw new InvalidOperationException("sh failed to start");
        await kill.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, kill.ExitCode);
    }

    /// <summary>Waits for the child to end and both its streams to be read to the end; kills it and throws past <paramref name="limit"/>.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }

        return process.ExitCode;
    }

    /// <summary>Polls until <paramref name="condition"/> holds; fails with <paramref name="what"/> and both streams past <paramref name="limit"/>.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan limit, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        while (!condition())
        {
            if (process.HasExited || timeout.IsCancellationRequested)
            {
                Kill();
                Assert.Fail($"{what}: gave up (exited: {process.HasExited})\nstdout: {Stdout}\nstderr: {Stderr}");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Kill();
        process.Dispose();
    }

    private void Append(StringBuilder into, TaskCompletionSource<bool> closed, string? line)
    {
        if (line is null)
        {
            closed.TrySetResult(true);
            return;
        }

        lock (gate)
            into.Append(line).Append('\n');
    }
}
