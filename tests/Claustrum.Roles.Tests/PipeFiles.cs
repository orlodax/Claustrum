using System.Diagnostics;
using System.Net.Sockets;

namespace Claustrum.Roles.Tests;

/// <summary>
/// A FIFO or a bound Unix socket standing where a regular file is expected (Unix only), and a bounded wait for the code
/// that must refuse it: a read that opens a FIFO blocks until a writer appears, so a regression shows up as the timeout
/// failure here and not as a test run that never ends. A machine without `mkfifo` skips the case with the reason.
/// </summary>
public static class PipeFiles
{
    private static readonly TimeSpan limit = TimeSpan.FromSeconds(20);

    public static void MakeFifo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("a FIFO cannot exist in a Windows filesystem.");
            return;
        }

        try
        {
            using Process process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path }, UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true })
                ?? throw new InvalidOperationException("mkfifo did not start");
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                Assert.Skip($"mkfifo failed here: {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Skip($"mkfifo is not available here: {ex.Message}");
        }
    }

    /// <summary>Binds an AF_UNIX socket at <paramref name="path"/>; the caller disposes it. Skips where Unix sockets cannot be bound.</summary>
    public static Socket BindSocket(string path)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("a filesystem socket node is Unix only here.");

        // macOS caps a socket path at 104 bytes and its real temp paths are long: bind at a short name, then rename the
        // node into place (a rename keeps the socket) (PR #116, first macOS CI run).
        string bindAt = path.Length < 100 ? path : Path.Combine("/tmp", $"clsk-{Guid.NewGuid():N}"[..13]);
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(bindAt));
            if (bindAt != path)
                File.Move(bindAt, path);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentOutOfRangeException)
        {
            socket.Dispose();
            Assert.Skip($"a Unix socket cannot be bound at '{path}': {ex.Message}");
        }

        return socket;
    }

    /// <summary>Runs <paramref name="body"/> and fails the test when it has not returned in the time limit (it is blocked on a pipe).</summary>
    public static T Within<T>(Func<T> body)
    {
        Task<T> task = Task.Run(body);
        if (!task.Wait(limit))
            throw new Xunit.Sdk.XunitException($"the call was still blocked after {limit.TotalSeconds:0} s: it waited on a pipe.");

        return task.Result;
    }

    public static async Task<T> WithinAsync<T>(Func<Task<T>> body)
    {
        Task<T> task = Task.Run(body);
        return await task.WaitAsync(limit);
    }
}
