using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Roles.Sync;

// #55 (review 2026-10-13): WINDOWS ONLY. The real path of an already OPENED file, so a link swapped in after a path
// check cannot be what is read (GetFinalPathNameByHandle), and its link count. Unix does not ask a handle for its
// path: PlainFile.OpenNoFollowUnder walks to the file without following a link (NOTES.md "How the handle is judged").
internal static partial class OpenedFilePath
{
    private const int MaxBuffer = 32768;
    private const int ByHandleInfoSize = 52;
    private const int LinksOffset = 40;

    /// <summary>The real path of the file <paramref name="handle"/> is (its own final path), or null when the OS cannot say or this is not Windows.</summary>
    public static string? TryGet(SafeFileHandle handle)
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsPath(handle) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the file <paramref name="handle"/> has other hard links (nNumberOfLinks over 1). Null when the OS
    /// cannot say; the caller fails closed.
    /// </summary>
    public static bool? HasOtherHardLinks(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        Span<byte> info = stackalloc byte[ByHandleInfoSize];
        return GetFileInformationByHandle(handle, info) ? BitConverter.ToUInt32(info[LinksOffset..]) > 1 : null;
    }

    /// <summary>
    /// The fence root spelled the way <see cref="TryGet"/> spells a path, so the two compare: the OS's own
    /// spelling (an 8.3 or mapped-drive cwd differs from the handle's). Null when the OS cannot place it.
    /// </summary>
    public static string? RootSpelling(SyncRoot fence) =>
        OperatingSystem.IsWindows() ? WindowsFinalPath.TryResolve(fence.Path) : null;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, Span<byte> information);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
