using System.Diagnostics;
using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Delegation;

// #124 stage 2 (NOTES.md "Detached hosts and tree routing"): hands a minted job to a `claustrum jobs host` process of its own,
// so the job's live session outlives the process that started it. The host inherits this environment and working directory
// (the env allow-list, the budget tree and the cast are all read from them), and no pipe of ours: its stdin is closed at
// once and its stdout and stderr are pipes nobody holds, which it replaces with its host.log before it writes a line.
internal static class DetachedLauncher
{
    /// <summary>
    /// Writes <paramref name="request"/> (the request as `run` parsed it, before Prepare added its warnings: the host prepares it
    /// again) beside the job, starts the host and records it in host.json. Throws when the host cannot be started.
    /// </summary>
    public static void Start(DelegateRequest request, JobPaths job)
    {
        File.WriteAllText(job.HostRequestJson, JsonSerializer.Serialize(request, DelegationJsonContext.Default.DelegateRequest));

        (string file, string[] leading) = SelfCommand();
        ProcessStartInfo startInfo = new()
        {
            FileName = file,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Windows: a console of its own that is never shown, so this one's Ctrl-C or close never reaches the host.
            CreateNoWindow = true,
        };
        foreach (string argument in leading)
            startInfo.ArgumentList.Add(argument);

        foreach (string argument in new[] { "jobs", "host", job.Id })
            startInfo.ArgumentList.Add(argument);

        using Process host = Process.Start(startInfo) ?? throw new IOException("the detached host did not start");
        host.StandardInput.Close();
        File.WriteAllText(job.HostJson, JsonSerializer.Serialize(new HostRecord(host.Id, new DateTimeOffset(host.StartTime)), LiveJsonContext.Default.HostRecord));
    }

    // This binary again. Under `dotnet claustrum.dll` (a development run) the process is `dotnet`, and the dll is its first argument.
    private static (string File, string[] Leading) SelfCommand()
    {
        string path = Environment.ProcessPath ?? throw new IOException("the running binary's own path is unknown");
        return Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (path, [Environment.GetCommandLineArgs()[0]])
            : (path, []);
    }
}
