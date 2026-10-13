using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Roles.Sync;

// 2026-10-13 (#100, #101): where a path lands is the OS's answer, not a string walk's: open a handle through the path
// and ask for its final name. That covers folder-mounted volumes, container bind mounts and cloud placeholders with no
// name list, and the root and a candidate both get the OS's spelling. Trap: the main open must NOT pass
// FILE_FLAG_OPEN_REPARSE_POINT, or links are not followed and the answer is the link's own place. Any failure leaves
// the path unplaced, which the fence refuses: over-refusal beats a stray write.
[SupportedOSPlatform("windows")]
internal static partial class WindowsFinalPath
{
    private const uint ShareReadWriteDelete = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint VolumeNameDos = 0;
    private const uint VolumeNameGuid = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int InitialBuffer = 1024;
    private const int MaxBuffer = 32768;

    /// <summary>
    /// The OS's own final path of <paramref name="fullyQualifiedPath"/>, every link on the way followed, spelled the way the
    /// fence compares: `X:\…`, `\\server\share\…`, or `\\?\Volume{…}\…` for a volume with no DOS name. The nearest
    /// existing ancestor is placed; the names below it have no directory entry, so cannot be links, and are
    /// appended as typed. Null when the OS cannot say, a dangling link included: the caller refuses.
    /// </summary>
    public static string? TryResolve(string fullyQualifiedPath)
    {
        string candidate;
        try
        {
            candidate = Path.GetFullPath(fullyQualifiedPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        Stack<string> missing = new();
        while (true)
        {
            using SafeFileHandle handle = Open(candidate, 0, out int error);
            if (!handle.IsInvalid)
            {
                string? final = FinalPathOf(handle);
                return final is null ? null : Path.Combine([final, .. missing]);
            }

            // An existing ancestor we cannot open may hide a link, so only "not found" pops a name.
            string trimmed = Path.TrimEndingDirectorySeparator(candidate);
            string? parent = Path.GetDirectoryName(trimmed);
            if (parent is null || error is not (ErrorFileNotFound or ErrorPathNotFound) || MayExist(candidate))
                return null;

            missing.Push(Path.GetFileName(trimmed));
            candidate = parent;
        }
    }

    // Unlike the main open this one stops at the entry itself: a dangling link has an entry but cannot be opened
    // through, and writing through a dangling file link creates its target, so it must not be popped as "missing".
    private static bool MayExist(string path)
    {
        using SafeFileHandle entry = Open(path, OpenReparsePoint, out int error);
        return !entry.IsInvalid || error is not (ErrorFileNotFound or ErrorPathNotFound);
    }

    private static SafeFileHandle Open(string path, uint extraFlags, out int error)
    {
        SafeFileHandle handle = CreateFile(Extended(path), 0, ShareReadWriteDelete, 0, OpenExisting, BackupSemantics | extraFlags, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    // Without the extended prefix CreateFileW stops at MAX_PATH and reports "path not found" for a path that exists.
    private static string Extended(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return full;

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static string? FinalPathOf(SafeFileHandle handle)
    {
        string? final = Query(handle, VolumeNameDos, out int error);

        // ERROR_PATH_NOT_FOUND: the volume has no DOS name the OS can spell, so ask for its GUID name instead.
        if (final is null && error == ErrorPathNotFound)
            final = Query(handle, VolumeNameGuid, out _);

        return final is null ? null : Plain(final);
    }

    private static unsafe string? Query(SafeFileHandle handle, uint flags, out int error)
    {
        char[] buffer = new char[InitialBuffer];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
                length = GetFinalPathNameByHandle(handle, chars, (uint)buffer.Length, flags);

            error = length == 0 ? Marshal.GetLastPInvokeError() : 0;
            if (length == 0)
                return null;

            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);

            // Too small: the return value is the size needed, terminator included.
            if (length >= MaxBuffer)
                return null;

            buffer = new char[length + 1];
        }
    }

    // `\\?\X:\…` → `X:\…` and `\\?\UNC\server\share\…` → `\\server\share\…`; a `\\?\Volume{…}\…` name stays as the OS spelled it.
    private static string Plain(string final)
    {
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + final[8..];

        bool driveLetter = final.Length > 5 && final.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(final[4]) && final[5] == ':';
        return driveLetter ? final[4..] : final;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
