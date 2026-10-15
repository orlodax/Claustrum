using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
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

    /// <summary>
    /// Creates <paramref name="path"/> with <paramref name="text"/> only if nothing is there, and never half-written: the text goes to a
    /// temp file that is then given its final name without replacing anything (<c>link(2)</c> on Unix, a move without overwrite on Windows,
    /// which refuse an existing target atomically). False when the file already existed: the first writer wins. .NET's own
    /// <c>File.Move(overwrite: false)</c> is no help on Unix: measured 2026-10-15, 8 threads moving to one target gave several winners in 284 of 300 rounds.
    /// </summary>
    public static bool TryCreate(string path, string text)
    {
        string temp = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
        File.WriteAllText(temp, text);
        try
        {
            return OperatingSystem.IsWindows() ? MoveExclusively(temp, path) : LinkExclusively(temp, path, text);
        }
        finally
        {
            // After a link the temp name is a second name for the file; after a move there is nothing at it.
            File.Delete(temp);
        }
    }

    private static bool MoveExclusively(string temp, string path)
    {
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    private static bool LinkExclusively(string temp, string path, string text)
    {
        if (Link(temp, path) == 0)
            return true;

        if (Marshal.GetLastPInvokeError() == Eexist)
            return false;

        // A filesystem with no hard links: create exclusively and write in place, so a reader can catch the file empty for the instant of one write.
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    private const int Eexist = 17;

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existing, string created);

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
