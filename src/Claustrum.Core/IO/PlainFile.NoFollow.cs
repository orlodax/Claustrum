using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Claustrum.Core.IO;

// #55 (review 2026-10-14, #110): the file is reached by a no-follow walk from the fence root, so a link swapped in
// at any moment is ELOOP/ENOTDIR, never a read. Nothing is compared after the fact. The flag values differ per OS
// and per Linux arch, and the nlink read has its own struct per OS: NOTES.md "How the handle is judged".
public static partial class PlainFile
{
    /// <summary>The refusal for a file with more than one name: another name may be outside the fence or under `.git`.</summary>
    public const string HardLinked = "the file has other hard links (one may be a file outside the working directory or inside .git)";

    private const int ENOTDIR = 20;
    private const int EMLINK = 31;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxNlink = 0x4;

    /// <summary>
    /// The regular file at <paramref name="components"/> below <paramref name="rootRealPath"/> (a fully resolved
    /// directory), opened for reading by walking it one component at a time with O_NOFOLLOW: a link anywhere on the way
    /// is a refusal, so the handle is by construction the file a link-free path names, however another process
    /// swaps the names meanwhile. Null with the reason in <paramref name="refusal"/> for a link, a non-regular file
    /// (<see cref="NotRegular"/>), a file with other hard links (<see cref="HardLinked"/>), or a platform whose flag
    /// values are not known here; a missing or unreadable component throws the usual <see cref="IOException"/>. Unix only.
    /// </summary>
    public static FileStream? OpenNoFollowUnder(string rootRealPath, IReadOnlyList<string> components, out string? refusal)
    {
        refusal = null;
        if (components.Count == 0 || components.Any(c => c.Length == 0 || c is "." or ".." || c.AsSpan().IndexOfAny('/', '\0') >= 0))
        {
            refusal = "the path below the working directory is not a plain list of names";
            return null;
        }

        if (WalkFlags() is not WalkFlagSet flags)
        {
            refusal = "no-follow open is not available on this platform";
            return null;
        }

        SafeFileHandle? current = Open(rootRealPath, ReadOnly | flags.Directory | flags.CloseOnExec);
        if (current.IsInvalid)
        {
            int errno = Marshal.GetLastPInvokeError();
            current.Dispose();
            throw OpenFailure(rootRealPath, errno);
        }

        try
        {
            for (int i = 0; i < components.Count; i++)
            {
                bool leaf = i == components.Count - 1;
                int open = ReadOnly | flags.NoFollow | flags.CloseOnExec | (leaf ? flags.NonBlock : flags.Directory);
                SafeFileHandle next = OpenAt(current, components[i], open);
                if (next.IsInvalid)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    next.Dispose();
                    if (IsLinkErrno(errno))
                    {
                        refusal = $"{components[i]} is a link (or not the directory expected) on the way";
                        return null;
                    }

                    if (leaf && IsSocketErrno(errno))
                    {
                        refusal = NotRegular;
                        return null;
                    }

                    throw OpenFailure(components[i], errno);
                }

                current.Dispose();
                current = next;
            }

            if (!IsRegular(current))
            {
                refusal = NotRegular;
                return null;
            }

            if (LinkCount(current) is not (>= 0 and <= 1))
            {
                refusal = HardLinked;
                return null;
            }

            FileStream stream = new(current, FileAccess.Read, 1);
            current = null;
            return stream;
        }
        finally
        {
            current?.Dispose();
        }
    }

    // ELOOP is 40 on Linux, 62 on macOS and FreeBSD; FreeBSD answers EMLINK for a link under O_NOFOLLOW.
    private static bool IsLinkErrno(int errno) => errno is ENOTDIR or EMLINK || errno == (OperatingSystem.IsLinux() ? 40 : 62);

    // The values differ per OS and, on Linux, per arch (arm64 keeps the 32-bit ARM O_DIRECTORY and O_NOFOLLOW):
    // linux x64 asm-generic/fcntl.h, linux arm64 arch/arm64/include/uapi/asm/fcntl.h, macOS xnu bsd/sys/fcntl.h,
    // FreeBSD sys/sys/fcntl.h. Null where they are not known: no walk, nothing inlined.
    private static WalkFlagSet? WalkFlags()
    {
        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => new(0x10000, 0x20000, 0x80000, 0x800),
                Architecture.Arm64 => new(0x4000, 0x8000, 0x80000, 0x800),
                _ => null,
            };
        }

        if (OperatingSystem.IsMacOS())
            return new(0x100000, 0x100, 0x1000000, 0x4);

        return OperatingSystem.IsFreeBSD() ? new(0x20000, 0x100, 0x100000, 0x4) : null;
    }

    // st_nlink of the open handle through libc (the runtime's FileStatus has none), or -1 when it cannot be read or the
    // buffer does not look like the struct expected (the type bits at its own mode offset must say "regular").
    private static long LinkCount(SafeFileHandle handle)
    {
        Span<byte> buffer = stackalloc byte[256];
        int fd = (int)handle.DangerousGetHandle();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // struct statx (linux/stat.h, arch-independent): stx_mask u32 at 0, stx_nlink u32 at 16, stx_mode u16 at 28.
                return StatX(fd, string.Empty, AtEmptyPath, StatxNlink, buffer) == 0
                    && (BitConverter.ToUInt32(buffer) & StatxNlink) != 0
                    && (BitConverter.ToUInt16(buffer[28..]) & TypeMask) == TypeRegular
                    ? BitConverter.ToUInt32(buffer[16..])
                    : -1;
            }

            if (OperatingSystem.IsMacOS())
            {
                // __DARWIN_STRUCT_STAT64 (sys/stat.h): st_dev int32 at 0, st_mode u16 at 4, st_nlink u16 at 6. On x64 the
                // plain `fstat` symbol is the 32-bit-inode layout kept for old binaries; `fstat$INODE64` is the one
                // whose layout this is. arm64 has only the 64-bit one, under the plain name.
                int status = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => FStatInode64(fd, buffer),
                    Architecture.Arm64 => FStatLibc(fd, buffer),
                    _ => -1,
                };

                return status == 0 && (BitConverter.ToUInt16(buffer[4..]) & TypeMask) == TypeRegular ? BitConverter.ToUInt16(buffer[6..]) : -1;
            }

            // FreeBSD 12+ struct stat (sys/sys/stat.h): st_dev u64 at 0, st_ino u64 at 8, st_nlink u64 at 16, st_mode u16 at 24.
            return OperatingSystem.IsFreeBSD() && FStatLibc(fd, buffer) == 0 && (BitConverter.ToUInt16(buffer[24..]) & TypeMask) == TypeRegular
                ? (long)BitConverter.ToUInt64(buffer[16..])
                : -1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private readonly record struct WalkFlagSet(int Directory, int NoFollow, int CloseOnExec, int NonBlock);

    // openat(2) is variadic in its mode; like `open` above it is called without one (no O_CREAT is ever passed).
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle OpenAt(SafeFileHandle directory, string name, int flags);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int fd, string path, int flags, uint mask, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "fstat")]
    private static partial int FStatLibc(int fd, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "fstat$INODE64")]
    private static partial int FStatInode64(int fd, Span<byte> buffer);
}
