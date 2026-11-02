using System.Diagnostics;
using System.Text;

namespace Claustrum.Core.Process;

// One "spawn a short-lived tool, read its output, bound the wait" call for every helper binary
// Claustrum shells out to — `git` (Core/Git/GitProcess.cs) and `gh` (Coordination/GhIssueSource.cs).
// Extracted from GitProcess so the stdin-close and timeout fixes of NOTES.md "MCP child stdin
// inheritance hung git" cannot be lost by a second call site hand-rolling ProcessStartInfo again.
// Not for backends: those go through ProcessRunner, which owns the env allow-list and the job logs.
public static class CommandProcess
{
    // `environment`, when given, is the child's whole environment (a worktree_hook's is EnvAllowList's, #146); null inherits this process's, as git and gh always did.
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, string cwd, string[] args, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        // .NET pre-fills Environment with this process's own; cleared first, or the allow-list is dead (NOTES.md "Env allow-list is dead…").
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string> entry in environment)
                startInfo.Environment[entry.Key] = entry.Value;
        }

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        process.Start();

        // Closed immediately: an inherited stdin would otherwise be the MCP host's own live
        // JSON-RPC pipe, and a child blocks reading from a pipe no one writes to (NOTES.md "MCP
        // child stdin inheritance hung git").
        process.StandardInput.Close();

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        // The reads are bounded by the timeout too: a background child the tool left (`sh -c "watcher &"`) holds stdout and stderr
        // open after the tool exits, and a read waiting for their end outlasted every timeout (#146 review, 2026-11-02).
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
        string stdout;
        string stderr;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (timeoutSource.IsCancellationRequested)
                throw new TimeoutException($"{exe} {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
            throw;
        }

        // The terminal's signal reached this child through the shared process group before the token was cancelled
        // (SignalExit): a cancel, not the tool saying no, for every git/gh call made under a run's token.
        if (cancellationToken.CanBeCanceled && SignalExit.IsCancelSignal(process.ExitCode) && await SignalExit.CancelArrivesAsync(cancellationToken))
            cancellationToken.ThrowIfCancellationRequested();

        return (process.ExitCode, stdout, stderr);
    }

    // A tool that already exited has no tree left to kill: a child it put in the background was handed to init and is not ours to find.
    private static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
