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
        if (!OperatingSystem.IsWindows())
            TrySetsid();

        TextWriter log = TextWriter.Synchronized(new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        Console.SetOut(log);
        Console.SetError(log);
    }

    private static void TrySetsid()
    {
        try
        {
            _ = Setsid();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No session of its own: the job still runs, and a hangup on the starter's terminal can reach it.
        }
    }

    [LibraryImport("libc", EntryPoint = "setsid")]
    private static partial int Setsid();
}
