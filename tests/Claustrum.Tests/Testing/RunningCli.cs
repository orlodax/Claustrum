using System.Diagnostics;
using System.Text;

namespace Claustrum.Tests.Testing;

/// <summary>
/// The built `claustrum` binary started through <see cref="Process"/> and left running, for the tests that signal it.
/// Started here and not through a shell `&amp;`: a non-interactive shell starts background jobs with SIGINT ignored and
/// .NET keeps an inherited ignore, so `kill -INT` would do nothing (NOTES.md "Ctrl-C reaches the runner").
/// The constructor's <c>keepStdinOpen</c> leaves the child's standard input open, so a read of it (`--brief-file -`) blocks.
/// </summary>
public sealed class RunningCli : IDisposable
{
    private readonly Process process;
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly Lock gate = new();
    private readonly TaskCompletionSource<bool> stdoutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunningCli(string binary, string workingDirectory, string home, string path, IEnumerable<string> args, bool keepStdinOpen = false, bool newSession = false)
    {
        (string File, string[] Arguments)? launcher = newSession ? SessionLauncher() : null;
        ProcessStartInfo startInfo = new()
        {
            FileName = launcher?.File ?? binary,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (launcher is { } session)
        {
            foreach (string launcherArgument in session.Arguments)
                startInfo.ArgumentList.Add(launcherArgument);

            startInfo.ArgumentList.Add(binary);
        }

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
        if (!keepStdinOpen)
            process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// What makes a child the leader of a session and process group of its own (`setsid`, so its pid is its pgid) and then
    /// becomes the binary: util-linux `setsid` where there is one, else perl or python3 (macOS has none of the three
    /// guaranteed but ships perl and python3). The host is not a group leader, so `setsid` does not fork and the pid is the binary's.
    /// </summary>
    private static (string File, string[] Arguments) SessionLauncher()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("process groups and sessions are POSIX.");

        if (OnPath("setsid"))
            return ("setsid", []);

        if (OnPath("perl"))
            return ("perl", ["-e", "use POSIX (); POSIX::setsid(); exec @ARGV or die $!;", "--"]);

        if (OnPath("python3"))
            return ("python3", ["-I", "-c", "import os, sys; os.setsid(); os.execv(sys.argv[1], sys.argv[1:])"]);

        Assert.Skip("none of setsid, perl or python3 is available to start the child in a session of its own.");
        return ("", []);
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, name)));

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
    /// True when the child started with signal <paramref name="number"/> ignored, so sending it would do nothing: the test
    /// host itself was launched from a background job of a non-interactive shell (SIGINT and SIGQUIT; 2 and 3) or under
    /// `nohup` (SIGHUP; 1). Linux reads `SigIgn` in /proc; where there is none (macOS) `ps -o ignored=` answers the same
    /// hex mask, and a host where neither can be read is taken as not ignoring.
    /// </summary>
    public bool IgnoresSignal(int number)
    {
        ulong? mask = IgnoredMask(process.Id);
        return mask is { } ignored && (ignored & (1UL << (number - 1))) != 0;
    }

    private static ulong? IgnoredMask(int pid)
    {
        string status = $"/proc/{pid}/status";
        if (File.Exists(status))
        {
            string? line = File.ReadLines(status).FirstOrDefault(l => l.StartsWith("SigIgn:", StringComparison.Ordinal));
            return line is null ? null : Convert.ToUInt64(line["SigIgn:".Length..].Trim(), 16);
        }

        try
        {
            ProcessStartInfo startInfo = new() { FileName = "ps", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ignored=");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using Process ps = Process.Start(startInfo) ?? throw new InvalidOperationException("ps failed to start");
            string output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return ps.ExitCode == 0 && ulong.TryParse(output, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong mask) ? mask : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>`kill -INT` through the shell builtin: what a terminal's Ctrl-C delivers to the process.</summary>
    public Task SendSigintAsync(CancellationToken cancellationToken) => SendSignalAsync("INT", cancellationToken);

    /// <summary>`kill -&lt;signal&gt; &lt;pid&gt;` through the shell builtin; <paramref name="signal"/> is a name without the `SIG` prefix (INT, TERM, QUIT, HUP).</summary>
    public async Task SendSignalAsync(string signal, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new() { FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"kill -{signal} {process.Id}");
        using Process kill = Process.Start(startInfo) ?? throw new InvalidOperationException("sh failed to start");
        await kill.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, kill.ExitCode);
    }

    /// <summary>
    /// `kill -s &lt;signal&gt; -- -&lt;pgid&gt;` <paramref name="times"/> times in one shell, back to back: the way a terminal or
    /// `kill -- -pgid` reaches the backend, git and hooks as well as claustrum. The child must have been started with
    /// <c>newSession</c>, so that its pid is its process group.
    /// </summary>
    public async Task SendGroupSignalAsync(string signal, CancellationToken cancellationToken, int times = 1)
    {
        ProcessStartInfo startInfo = new() { FileName = "/bin/sh", UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(string.Join("; ", Enumerable.Repeat($"kill -s {signal} -- -{process.Id}", times)));
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
