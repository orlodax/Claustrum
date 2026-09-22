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
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, string cwd, string[] args, TimeSpan timeout, CancellationToken cancellationToken)
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

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        process.Start();

        // Closed immediately: an inherited stdin would otherwise be the MCP host's own live
        // JSON-RPC pipe, and a child blocks reading from a pipe no one writes to (NOTES.md "MCP
        // child stdin inheritance hung git").
        process.StandardInput.Close();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (timeoutSource.IsCancellationRequested)
                throw new TimeoutException($"{exe} {string.Join(' ', args)} timed out after {timeout.TotalSeconds}s");
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
