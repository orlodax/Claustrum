using System.Diagnostics;
using System.Text;

namespace Claustrum.Core.Git;

// Shared `git` child-process invocation for WorktreeSnapshot and JobWorktree. Carries the stdin
// and timeout fixes from NOTES.md "MCP child stdin inheritance hung git" so a second call site
// cannot re-introduce that hang by hand-rolling ProcessStartInfo again.
public static class GitProcess
{
    // Defense-in-depth on top of the stdin fix below: with a child's stdin properly closed rather
    // than inherited, this should never fire, but a stuck git must not be able to wedge a tool call
    // forever the way it did before this fix (NOTES.md "MCP child stdin inheritance hung git").
    private static readonly TimeSpan gitTimeout = TimeSpan.FromSeconds(30);

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string cwd, string[] args, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
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
        // JSON-RPC pipe, and git blocks reading from a pipe no one writes to (NOTES.md "MCP child
        // stdin inheritance hung git").
        process.StandardInput.Close();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutSource = new(gitTimeout);
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
                throw new TimeoutException($"git {string.Join(' ', args)} timed out after {gitTimeout.TotalSeconds}s");
            throw;
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
