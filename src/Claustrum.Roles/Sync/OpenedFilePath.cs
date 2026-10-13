using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Roles.Sync;

// #55 (review 2026-10-13): the real path of an already OPENED file, asked of the OS about the handle, never about a
// path string, so a link swapped in after a path check cannot be what is read. Null when the OS cannot say; the caller
// fails closed. macOS asks proc_pidfdinfo, not fcntl(F_GETPATH): fcntl is variadic and Apple arm64 passes variadic
// arguments on the stack, which a P/Invoke does not do (NOTES.md "The api backend gets AGENTS.md and CLAUDE.md inlined").
internal static partial class OpenedFilePath
{
    private const int MaxBuffer = 32768;

    // <sys/proc_info.h>: struct vnode_fdinfowithpath = proc_fileinfo (24) + vnode_info (152) + vip_path[MAXPATHLEN].
    private const int ProcPidFdVnodePathInfo = 2;
    private const int MacPathOffset = 24 + 152;
    private const int MacPathLength = 1024;

    public static string? TryGet(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsPath(handle);

            if (OperatingSystem.IsLinux())
                return new FileInfo($"/proc/self/fd/{(int)handle.DangerousGetHandle()}").LinkTarget;

            return OperatingSystem.IsMacOS() ? MacPath(handle) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The fence root spelled the way <see cref="TryGet"/> spells a path, so the two compare: on Windows the OS's own
    /// spelling (an 8.3 or mapped-drive cwd differs from the handle's), elsewhere the resolved root. Null when the OS cannot place it.
    /// </summary>
    public static string? RootSpelling(SyncRoot fence) =>
        OperatingSystem.IsWindows() ? WindowsFinalPath.TryResolve(fence.Path) : fence.RealPath;

    [SupportedOSPlatform("windows")]
    private static unsafe string? WindowsPath(SafeFileHandle handle)
    {
        char[] buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
                length = GetFinalPathNameByHandle(handle, chars, (uint)buffer.Length, 0);

            if (length == 0)
                return null;

            if (length < buffer.Length)
                return Plain(new string(buffer, 0, (int)length));

            // Too small: the return value is the size needed, terminator included.
            if (length >= MaxBuffer)
                return null;

            buffer = new char[length + 1];
        }
    }

    // `\\?\X:\…` → `X:\…`, `\\?\UNC\s\…` → `\\s\…`: the spelling WindowsFinalPath gives the root.
    private static string Plain(string final)
    {
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + final[8..];

        bool driveLetter = final.Length > 5 && final.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(final[4]) && final[5] == ':';
        return driveLetter ? final[4..] : final;
    }

    [SupportedOSPlatform("macos")]
    private static unsafe string? MacPath(SafeFileHandle handle)
    {
        byte[] buffer = new byte[MacPathOffset + MacPathLength];
        int filled;
        fixed (byte* bytes = buffer)
            filled = ProcPidFdInfo(Environment.ProcessId, (int)handle.DangerousGetHandle(), ProcPidFdVnodePathInfo, bytes, buffer.Length);

        // A short fill means the struct is not the one this layout describes: no answer, never a guess.
        if (filled != buffer.Length)
            return null;

        int end = Array.IndexOf(buffer, (byte)0, MacPathOffset);
        return end <= MacPathOffset ? null : System.Text.Encoding.UTF8.GetString(buffer, MacPathOffset, end - MacPathOffset);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    [SupportedOSPlatform("macos")]
    [LibraryImport("libc", EntryPoint = "proc_pidfdinfo")]
    private static unsafe partial int ProcPidFdInfo(int pid, int fd, int flavor, byte* buffer, int bufferSize);
}
