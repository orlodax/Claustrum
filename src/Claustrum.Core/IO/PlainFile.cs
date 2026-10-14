using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Core.IO;

// 2026-10-13 (found by review, #55/#97): `File.Exists` is true for a FIFO and `File.OpenRead` then blocks in
// open(O_RDONLY) until a writer appears, so a `mkfifo AGENTS.md` hung the process. On Unix the file is opened
// O_NONBLOCK (a regular file ignores it), and the OPENED handle is asked whether it seeks: no path is stat-ed
// first, which would reopen the swap race the handle-based checks close. Windows has no FIFO in the filesystem,
// so the plain open stays. Lives in Core since the second review (NOTES.md "A FIFO in place of a tracked file").
public static partial class PlainFile
{
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int EISDIR = 21;
    private const int ENXIO = 6;
    private const int ReadOnly = 0;
    private const string NotRegular = "not a regular file (a pipe or a socket is never read)";

    /// <summary>
    /// The file opened for reading, or null with the reason in <paramref name="refusal"/> when it is not a regular
    /// file: a FIFO (the handle does not seek) or a socket (open fails with ENXIO, or EOPNOTSUPP on macOS and
    /// FreeBSD); an unreadable file throws the usual <see cref="IOException"/>.
    /// </summary>
    public static FileStream? OpenRead(string path, out string? refusal)
    {
        refusal = null;
        if (UnixFlags() is not int flags)
            return File.OpenRead(path);

        SafeFileHandle handle = Open(path, flags);
        if (handle.IsInvalid)
        {
            int errno = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (IsSocketErrno(errno))
            {
                refusal = NotRegular;
                return null;
            }

            string message = $"{path}: {Marshal.GetPInvokeErrorMessage(errno)}";
            throw errno switch
            {
                ENOENT => new FileNotFoundException(message, path),
                EACCES or EISDIR => new UnauthorizedAccessException(message),
                _ => new IOException(message),
            };
        }

        FileStream stream = new(handle, FileAccess.Read, 1);
        if (stream.CanSeek)
            return stream;

        stream.Dispose();
        refusal = NotRegular;
        return null;
    }

    /// <summary>
    /// Whether a path segment names the `.git` directory on this OS: Windows also reaches it as `.git.`, `.git ` and
    /// the 8.3 short name `GIT~1`. Case is ignored everywhere (macOS volumes are usually case-insensitive).
    /// </summary>
    public static bool IsGitDirName(string segment)
    {
        if (!OperatingSystem.IsWindows())
            return segment.Equals(".git", StringComparison.OrdinalIgnoreCase);

        // `.git::$INDEX_ALLOCATION` names the directory itself (CVE-2019-1352): the stream suffix goes first (review 2026-10-13).
        int colon = segment.IndexOf(':');
        string name = (colon < 0 ? segment : segment[..colon]).TrimEnd('.', ' ');
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || (name.Length > 4 && name.StartsWith("git~", StringComparison.OrdinalIgnoreCase) && name[4..].All(char.IsAsciiDigit));
    }

    // open(2) on a Unix socket: ENXIO on Linux, macOS and FreeBSD; EOPNOTSUPP (102 / 45) on macOS and FreeBSD as well.
    private static bool IsSocketErrno(int errno) => errno == ENXIO
        || (OperatingSystem.IsMacOS() && errno == 102)
        || (OperatingSystem.IsFreeBSD() && errno == 45);

    // O_RDONLY | O_NONBLOCK | O_CLOEXEC; the values differ per OS. Null: Windows, or a Unix whose values are not known here.
    private static int? UnixFlags()
    {
        if (OperatingSystem.IsLinux())
            return ReadOnly | 0x800 | 0x80000;

        if (OperatingSystem.IsMacOS())
            return ReadOnly | 0x4 | 0x1000000;

        return OperatingSystem.IsFreeBSD() ? ReadOnly | 0x4 | 0x100000 : null;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle Open(string path, int flags);
}
