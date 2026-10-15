using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Core.IO;

// A file a reader must never see half-written (host.json, host-request.json, owner.json): the text goes to a temp file beside it and
// is renamed over the target. A plain Windows rename is refused while a reader holds the target, even with FileShare.Delete (CI,
// 2026-10-15), so Windows renames with POSIX semantics, which replace an open file; only where the volume lacks them is it retried.
public static partial class AtomicFile
{
    private const int Attempts = 12;

    public static void WriteAllText(string path, string text)
    {
        string temp = $"{path}.tmp";
        File.WriteAllText(temp, text);
        if (OperatingSystem.IsWindows() && TryPosixRename(temp, path))
            return;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                {
                    Thread.Sleep(Math.Min(10 << attempt, 250));
                    continue;
                }

                File.Delete(temp);
                throw;
            }
        }
    }

    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    // FILE_RENAME_INFO: a 4-byte Flags union, then the pointer-aligned RootDirectory handle, a 4-byte name length, then the
    // UTF-16 name. False when the volume (FAT, some network shares) has no POSIX rename, so the caller falls back to File.Move.
    [SupportedOSPlatform("windows")]
    private static bool TryPosixRename(string source, string destination)
    {
        using SafeFileHandle handle = CreateFileW(Path.GetFullPath(source), Delete | Synchronize, ShareAll, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
            return false;

        byte[] name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination) + "\0");
        int nameOffset = IntPtr.Size + IntPtr.Size + sizeof(uint);
        byte[] info = new byte[nameOffset + name.Length];
        BitConverter.TryWriteBytes(info.AsSpan(0, sizeof(uint)), ReplaceIfExists | PosixSemantics);
        BitConverter.TryWriteBytes(info.AsSpan(IntPtr.Size + IntPtr.Size, sizeof(uint)), (uint)(name.Length - sizeof(char)));
        name.CopyTo(info, nameOffset);
        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, uint size);
}
