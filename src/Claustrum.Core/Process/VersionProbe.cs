using System.Diagnostics;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

// Shared "run <binary> <versionArgs>, read stdout, apply a timeout" probe for IBackend.DetectAsync
// implementations. Carries the same stdin-close fix as GitProcess (Core/Git/GitProcess.cs) so a
// detect probe cannot hang under a real MCP host (NOTES.md "MCP child stdin inheritance hung git") —
// extracted from ClaudeBackend's own DetectAsync once opencode/cursor/copilot/api needed the same
// locate-spawn-timeout-interpret shape (docs/PLAN.md §A3 "Adding a backend = one class...").
public static class VersionProbe
{
    private static readonly TimeSpan defaultTimeout = TimeSpan.FromSeconds(10);

    public static async Task<Doctor> RunAsync(string name, string[] versionArgs, BackendConfig? config, IPlatform platform, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ResolvedBinary binary;
        try
        {
            binary = BinaryLocator.Locate(name, versionArgs, config, platform);
        }
        catch (BackendNotFoundException)
        {
            return new Doctor(false, null, null, [$"'{name}' was not found on PATH"]);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = binary.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in binary.Args)
            startInfo.ArgumentList.Add(arg);

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();

            // Closed immediately, not inherited from the MCP host's own live stdio pipe (NOTES.md
            // "MCP child stdin inheritance hung git").
            process.StandardInput.Close();
        }
        catch (Exception ex)
        {
            return new Doctor(false, binary.Executable, null, [$"'{name} {string.Join(' ', versionArgs)}' failed to start: {ex.Message}"]);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        TimeSpan effectiveTimeout = timeout ?? defaultTimeout;
        using CancellationTokenSource timeoutSource = new(effectiveTimeout);
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
                return new Doctor(false, binary.Executable, null, [$"'{name} {string.Join(' ', versionArgs)}' timed out after {effectiveTimeout.TotalSeconds}s"]);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return process.ExitCode == 0
            ? new Doctor(true, binary.Executable, stdout.Trim(), [])
            : new Doctor(false, binary.Executable, null, [$"'{name} {string.Join(' ', versionArgs)}' exited with code {process.ExitCode}: {stderr.Trim()}"]);
    }
}
