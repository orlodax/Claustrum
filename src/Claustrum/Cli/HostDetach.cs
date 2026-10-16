using System.Runtime.InteropServices;

namespace Claustrum.Cli;

// What the `jobs host` process does first (#124 stage 2), so that nothing of the terminal it was started from reaches it. The launcher
// has already given it a closed stdin and pipes nobody holds (and on Windows a console of its own); this makes it a session of its
// own on Unix, where a hangup is delivered to a session's foreground group and a terminal's Ctrl-C to its own, and
// sends everything it writes to the job's host.log.
internal static partial class HostDetach
{
    public static void Apply(string logPath)
    {
        // Fails when the process already leads a group, which only means it was detached before; nothing to do either way. A libc the
        // runtime cannot resolve costs the session, not the job.
        string detached = OperatingSystem.IsWindows() ? "windows: no session to leave" : TrySetsid();

        TextWriter log = TextWriter.Synchronized(new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        Console.SetOut(log);
        Console.SetError(log);

        // What the host is detached from is the one thing a signal reaching it on macOS CI could not be told by (2026-10-16): say it.
        log.WriteLine($"claustrum jobs host {Environment.ProcessId}: {detached}");
    }

    private static string TrySetsid()
    {
        try
        {
            int before = GetSid(0);
            int result = Setsid();
            int error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            return $"setsid() = {result} (errno {error}); session {before} -> {GetSid(0)}, group {GetPgid(0)}";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No session of its own: the job still runs, and a hangup on the starter's terminal can reach it.
            return $"setsid unavailable ({e.GetType().Name}: {e.Message})";
        }
    }

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int Setsid();

    [LibraryImport("libc", EntryPoint = "getsid")]
    private static partial int GetSid(int pid);

    [LibraryImport("libc", EntryPoint = "getpgid")]
    private static partial int GetPgid(int pid);
}
