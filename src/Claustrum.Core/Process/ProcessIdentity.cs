using System.ComponentModel;

namespace Claustrum.Core.Process;

// Whether the process a pid and a start time were recorded for is still that process. Shared by live.json's owner (#124) and
// a detached job's host.json: a pid alone proves nothing once it can be reused.
public static class ProcessIdentity
{
    // Process start times come from the OS clock and are not exact across reads (Linux derives them from the
    // boot time): a reused pid belongs to a process started long after the owner, never within seconds of it.
    private static readonly TimeSpan startSkew = TimeSpan.FromSeconds(5);

    /// <summary>
    /// False when the process is gone, or its pid now belongs to another process (the start time differs). True when it
    /// is alive, when no start time was recorded, or when it exists but cannot be inspected: nothing is declared dead on a guess.
    /// </summary>
    public static bool IsAlive(int pid, DateTimeOffset? startedAt)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && (startedAt is not { } started || (process.StartTime.ToUniversalTime() - started).Duration() <= startSkew);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }
}
