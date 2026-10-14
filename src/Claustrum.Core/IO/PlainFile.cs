using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Core.IO;

// 2026-10-13 (found by review, #55/#97): `File.Exists` is true for a FIFO and `File.OpenRead` then blocks in
// open(O_RDONLY) until a writer appears. On Unix a file is opened O_NONBLOCK (a regular file ignores it) and the
// OPENED handle must be a regular file: no path is stat-ed first (swap race). 2026-10-14 (found by review): CanSeek
// let /dev/zero through, so the type comes from fstat on the handle, and writes get the same door (TryOpenWrite).
// Windows has no FIFO in its filesystem, so the plain open stays. NOTES.md "A pipe, socket or device in place of a repo file".
public static partial class PlainFile
{
    /// <summary>The most text <see cref="TryReadAllText"/> reads; a repo file is never that big (2026-10-14).</summary>
    public const int MaxTextBytes = 8 * 1024 * 1024;

    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int EISDIR = 21;
    private const int ENXIO = 6;
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int TypeMask = 0xF000;
    private const int TypeRegular = 0x8000;
    private const string NotRegular = "not a regular file (a pipe, socket or device is never read or written)";
    private const string TooLarge = "not a regular file or too large (over 8 MiB)";

    /// <summary>
    /// The file opened for reading, or null with the reason in <paramref name="refusal"/> when it is not a regular
    /// file: a FIFO, a device (the handle's fstat says so) or a socket (open fails with ENXIO, or EOPNOTSUPP on
    /// macOS and FreeBSD); an unreadable file throws the usual <see cref="IOException"/>.
    /// </summary>
    public static FileStream? OpenRead(string path, out string? refusal)
    {
        refusal = null;
        if (UnixFlags(ReadOnly) is not int flags)
            return File.OpenRead(path);

        SafeFileHandle? handle = OpenRegular(path, flags, out refusal);
        return handle is null ? null : new(handle, FileAccess.Read, 1);
    }

    /// <summary>
    /// The file opened for writing in place (the mode and the inode survive, a read-only directory is fine), or null
    /// with the reason in <paramref name="refusal"/> when the target is a FIFO, a device or a socket. The type is
    /// checked on the write handle, so there is no check-then-write gap. <paramref name="mode"/> is
    /// <see cref="FileMode.Create"/> (create or truncate) or <see cref="FileMode.Append"/> (create or write at the end).
    /// </summary>
    public static FileStream? TryOpenWrite(string path, FileMode mode, out string? refusal)
    {
        if (mode is not (FileMode.Create or FileMode.Append))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Create and Append are supported.");

        refusal = null;
        if (UnixFlags(WriteOnly) is not int flags)
        {
            if (!Directory.Exists(path))
                return new(path, mode, FileAccess.Write);

            refusal = NotRegular;
            return null;
        }

        // open(2) takes the creation mode as a variadic, which Apple arm64 passes on the stack where a LibraryImport
        // cannot put it: a missing file is created by .NET (O_EXCL, never through a FIFO) and then opened without O_CREAT.
        try
        {
            new FileStream(path, FileMode.CreateNew, FileAccess.Write).Dispose();
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
        }

        SafeFileHandle? handle = OpenRegular(path, flags, out refusal);
        if (handle is null)
            return null;

        FileStream stream = new(handle, FileAccess.Write, 1);
        if (mode == FileMode.Append)
            stream.Seek(0, SeekOrigin.End);
        else
            stream.SetLength(0);

        return stream;
    }

    /// <summary>
    /// <paramref name="text"/> written in place (UTF-8, no BOM, like <see cref="File.WriteAllText(string, string?)"/>
    /// and <see cref="File.AppendAllText(string, string?)"/>), or false with the reason in
    /// <paramref name="refusal"/> when <see cref="TryOpenWrite"/> refuses the target.
    /// </summary>
    public static bool TryWriteAllText(string path, string text, FileMode mode, [NotNullWhen(false)] out string? refusal)
    {
        using FileStream? stream = TryOpenWrite(path, mode, out refusal);
        if (stream is null)
        {
            refusal ??= NotRegular;
            return false;
        }

        using StreamWriter writer = new(stream);
        writer.Write(text);
        return true;
    }

    /// <summary>
    /// The whole file as text (UTF-8, BOM honoured, like <see cref="File.ReadAllText(string)"/>), or false with the
    /// reason in <paramref name="refusal"/> when <see cref="OpenRead"/> refuses it or it passes
    /// <see cref="MaxTextBytes"/>.
    /// </summary>
    public static bool TryReadAllText(string path, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? refusal)
    {
        text = null;
        using FileStream? stream = OpenRead(path, out refusal);
        if (stream is null)
        {
            refusal ??= NotRegular;
            return false;
        }

        using MemoryStream bytes = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            bytes.Write(chunk, 0, read);
            if (bytes.Length <= MaxTextBytes)
                continue;

            refusal = TooLarge;
            return false;
        }

        bytes.Position = 0;
        using StreamReader reader = new(bytes);
        text = reader.ReadToEnd();
        return true;
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
    // A FIFO opened for writing with no reader fails with ENXIO too.
    private static bool IsSocketErrno(int errno) => errno == ENXIO
        || (OperatingSystem.IsMacOS() && errno == 102)
        || (OperatingSystem.IsFreeBSD() && errno == 45);

    // access | O_NONBLOCK | O_CLOEXEC; the values differ per OS. Null: Windows, or a Unix whose values are not known here.
    private static int? UnixFlags(int access)
    {
        if (OperatingSystem.IsLinux())
            return access | 0x800 | 0x80000;

        if (OperatingSystem.IsMacOS())
            return access | 0x4 | 0x1000000;

        return OperatingSystem.IsFreeBSD() ? access | 0x4 | 0x100000 : null;
    }

    // open + fstat on the handle; null with the refusal for a socket, a FIFO nobody reads, or any non-regular file.
    private static SafeFileHandle? OpenRegular(string path, int flags, out string? refusal)
    {
        refusal = null;
        SafeFileHandle handle = Open(path, flags);
        if (handle.IsInvalid)
        {
            int errno = Marshal.GetLastPInvokeError();
            handle.Dispose();

            // Only a write open of a directory fails with EISDIR (a read open succeeds and fstat refuses it below): a refusal, not an exception.
            if (IsSocketErrno(errno) || errno == EISDIR)
            {
                refusal = NotRegular;
                return null;
            }

            string message = $"{path}: {Marshal.GetPInvokeErrorMessage(errno)}";
            throw errno switch
            {
                ENOENT => new FileNotFoundException(message, path),
                EACCES => new UnauthorizedAccessException(message),
                _ => new IOException(message),
            };
        }

        if (IsRegular(handle))
            return handle;

        handle.Dispose();
        refusal = NotRegular;
        return null;
    }

    // The .NET runtime's own shim (it is linked into a NativeAOT binary) fills a FileStatus whose layout is fixed on
    // every OS, unlike libc's struct stat: Flags (int32) at 0 then Mode (int32) at 4, which holds st_mode. Anything
    // the shim cannot answer is not regular: fail closed.
    private static bool IsRegular(SafeFileHandle handle)
    {
        Span<byte> status = stackalloc byte[256];
        try
        {
            return FStat(handle.DangerousGetHandle(), status) == 0
                && (BitConverter.ToInt32(status[4..]) & TypeMask) == TypeRegular;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle Open(string path, int flags);

    [LibraryImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static partial int FStat(nint fd, Span<byte> status);
}
