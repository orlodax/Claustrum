using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claustrum.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Roles.Sync;

// #55 (review 2026-10-13): the real path of an already OPENED file, so a link swapped in after a path check cannot be
// what is read. Windows asks the OS about the handle (GetFinalPathNameByHandle). Unix (Linux and macOS alike, #110,
// 2026-10-14) proves the handle IS the file at the fully resolved path, by (device, inode) of fstat vs stat: no per-OS
// handle-to-path call, so no struct layout to get wrong. Null when that cannot be shown; the caller fails closed
// (NOTES.md "The api backend gets AGENTS.md and CLAUDE.md inlined").
internal static partial class OpenedFilePath
{
    private const int MaxBuffer = 32768;

    /// <summary>
    /// The real path of the file <paramref name="handle"/> (opened through <paramref name="path"/>) is, or null when the
    /// OS cannot say. Windows: the handle's own final path. Unix: the path <paramref name="path"/> resolves to, but only
    /// when the handle is that file; it is resolved on both sides of the stat, so a link flipped in between is null.
    /// </summary>
    public static string? TryGet(SafeFileHandle handle, string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsPath(handle);

            string real = SyncRoot.Resolve(path, out bool unresolved);
            return !unresolved && PlainFile.IsSameFile(handle, real) && SyncRoot.Resolve(path) == real ? real : null;
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

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
