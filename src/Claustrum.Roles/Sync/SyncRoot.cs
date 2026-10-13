using IoPath = System.IO.Path;

namespace Claustrum.Roles.Sync;

/// <summary>
/// The directory a sync writes under and, in repo mode, the fence that keeps it there: a target whose
/// real path (every link on the way resolved) lies outside <see cref="RealPath"/>, or cannot be resolved
/// (a link loop; on Windows a link the string walk cannot place and the OS cannot either), is refused.
/// Where the string walk cannot place a Windows link (an NT-namespace target, a folder-mounted volume, an
/// unreadable reparse point) the OS says where the path lands (<c>WindowsFinalPath</c>, issues #100, #101).
/// Under `--global` the root is the user's own and unfenced. NOTES.md "`sync` never writes outside its
/// root through a linked directory" holds the rules and the Windows notes (issues #95, #93).
/// The walk never opens a network-spelled path outside the starting path's own share (#105, NOTES.md
/// "UNC targets are refused without opening").
/// </summary>
public sealed class SyncRoot
{
    private const int MaxLinkHops = 40;

    private static readonly string[] networkDevices = ["Mup", "LanmanRedirector", "WebDavRedirector"];
    private static readonly string[] uncPrefixes = [@"\\?\UNC\", @"\\.\UNC\", @"\??\UNC\"];

    // What FileSystemInfo.Attributes reads for a path that does not exist or cannot be stat'ed.
    private const FileAttributes NoSuchEntry = (FileAttributes)(-1);

    private static int osPlacements;

    private readonly Lazy<string?> canonicalRealPath;

    private SyncRoot(string path, bool fenced)
    {
        Path = path;
        RealPath = Resolve(path);
        Fenced = fenced;

        // The root in the OS's own spelling: the only one an OS-placed candidate may be compared with,
        // never RealPath, which may be as typed (an 8.3 `RUNNER~1` cwd).
        canonicalRealPath = new(() => PlaceByOs(path));
    }

    /// <summary>The root as given: the cwd in repo mode, the global root under `--global`.</summary>
    public string Path { get; }

    public string RealPath { get; }

    public bool Fenced { get; }

    public static SyncRoot Repo(string cwd) => new(cwd, fenced: true);

    public static SyncRoot Global(string root) => new(root, fenced: false);

    /// <summary>How many times the walk has asked the OS to place a path: the seam that proves a network target never reached it (#105).</summary>
    internal static int OsPlacements => Volatile.Read(ref osPlacements);

    /// <summary>
    /// True when <paramref name="path"/> names a network location in any spelling Windows resolves, so the walk can
    /// refuse it on the string, before any call that would authenticate to the host (#105): `\\server\share`, `\\?\UNC\`,
    /// `\\.\UNC\`, `\??\UNC\` in any letter case, `GLOBALROOT\Device\Mup` (also behind `Global\`), `\Device\Mup`.
    /// Local device syntax (`\\?\C:\`, `\\.\pipe`) and drive or relative paths are not network. Pure string logic.
    /// </summary>
    public static bool IsNetworkSpelled(string path)
    {
        string spelled = path.Replace('/', '\\');
        if (spelled.StartsWith(@"\\?\", StringComparison.Ordinal) || spelled.StartsWith(@"\\.\", StringComparison.Ordinal) || spelled.StartsWith(@"\??\", StringComparison.Ordinal))
            return IsNetworkName(spelled.AsSpan(4));

        // Any other `\\` is a UNC path, `\\?` and `\\.` without a separator included: fail closed.
        if (spelled.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // A single leading backslash is drive-relative unless it names the NT object namespace.
        string rooted = spelled.TrimStart('\\');
        return spelled.StartsWith('\\')
            && (rooted.StartsWith("Device\\", StringComparison.OrdinalIgnoreCase) || rooted.StartsWith("GLOBAL??\\", StringComparison.OrdinalIgnoreCase))
            && IsNetworkName(rooted);
    }

    // The object-namespace remainder after `\\?\`: network when its leading names (ignoring the aliases that
    // lead into the root namespace) reach `UNC` or one of the network provider devices.
    private static bool IsNetworkName(ReadOnlySpan<char> name)
    {
        while (true)
        {
            name = name.TrimStart('\\');
            int end = name.IndexOf('\\');
            ReadOnlySpan<char> first = end < 0 ? name : name[..end];
            ReadOnlySpan<char> rest = end < 0 ? [] : name[end..];
            if (first.Equals("UNC", StringComparison.OrdinalIgnoreCase))
                return true;

            if (first.Equals("Device", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest.TrimStart('\\');
                int deviceEnd = rest.IndexOf('\\');
                ReadOnlySpan<char> device = deviceEnd < 0 ? rest : rest[..deviceEnd];
                foreach (string provider in networkDevices)
                {
                    if (device.Equals(provider, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            if (!(first.Equals("Global", StringComparison.OrdinalIgnoreCase) || first.Equals("GLOBAL??", StringComparison.OrdinalIgnoreCase)
                || first.Equals("GLOBALROOT", StringComparison.OrdinalIgnoreCase) || first.Equals("??", StringComparison.Ordinal)))
                return false;

            name = rest;
        }
    }

    // `\\server\share` of a path that names one, in the plain spelling whatever the input's; null for anything else.
    private static string? ShareOf(string path)
    {
        string spelled = path.Replace('/', '\\');
        foreach (string prefix in uncPrefixes)
        {
            if (spelled.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                spelled = @"\\" + spelled[prefix.Length..];
                break;
            }
        }

        if (!spelled.StartsWith(@"\\", StringComparison.Ordinal) || spelled.StartsWith(@"\\?\", StringComparison.Ordinal) || spelled.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;

        string[] names = spelled[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return names.Length < 2 ? null : $@"\\{names[0]}\{names[1]}";
    }

    // A network-spelled path the walk may not touch: the only network path it may is on the share the walk started on,
    // where the process already is (a repo whose cwd is on a share). Windows only; elsewhere these are ordinary names.
    private static bool IsForeignNetworkPath(string path, string? ownShare) =>
        OperatingSystem.IsWindows()
        && IsNetworkSpelled(path)
        && (ShareOf(path) is not { } share || !share.Equals(ownShare, StringComparison.OrdinalIgnoreCase));

    public static string Resolve(string path) => Resolve(path, out _);

    /// <summary>
    /// The real path of <paramref name="path"/>: every link on the way, directories and leaf alike, is
    /// followed; trailing components that do not exist yet stay as typed. On Windows a component the string
    /// walk cannot place (an opaque link target, an unreadable link or reparse point) is placed by the OS and
    /// the result is the OS's path. <paramref name="unresolved"/> is true when the walk could not be finished:
    /// the 40-hop cap was hit, or the OS could not place such a component; the result is then the path as far
    /// as it was resolved. `..` follows the OS: physical on Unix, lexical on Windows (NOTES.md, same section).
    /// </summary>
    public static string Resolve(string path, out bool unresolved) => Walk(path, OperatingSystem.IsWindows(), out unresolved, out _);

    /// <summary>
    /// The cheap string gate kept beside the structural <see cref="IsOpaqueWindowsLink(FileSystemInfo)"/>: a
    /// not-fully-qualified target that names the NT object namespace (`GLOBALROOT`, `Volume{`, `?`, `:`).
    /// It costs nothing and still fires if the runtime's immediate-target string ever changed shape (#95 review).
    /// </summary>
    public static bool IsOpaqueWindowsLinkTarget(string target)
    {
        return !IoPath.IsPathFullyQualified(target)
            && (target.StartsWith("GLOBALROOT", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)
                || target.Contains('?')
                || target.Contains(':'));
    }

    /// <summary>
    /// Windows: true when <paramref name="link"/> stores an absolute reparse target .NET could not turn into a
    /// Win32 path. ResolveLinkTarget(false) joins a relative target onto the link's directory and leaves an
    /// absolute one with its first four characters dropped, and ToString() keeps that string (FullName would
    /// re-root it): a result that is not fully qualified is an NT-namespace target, whatever its first component
    /// (#95 review). The walk does not refuse such a link outright: it asks the OS where it lands (#100).
    /// Always false off Windows, where the walk never calls it.
    /// </summary>
    public static bool IsOpaqueWindowsLink(FileSystemInfo link)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            FileSystemInfo? immediate = link.ResolveLinkTarget(returnFinalTarget: false);
            return immediate is null || !IoPath.IsPathFullyQualified(immediate.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A link whose structural read fails, for any reason the runtime reports, is opaque, never "not a
            // link" (2026-10-12: an empty `\??\` target throws ArgumentException from GetFullPath in FileInfo's constructor).
            return true;
        }
    }

    /// <summary>The same check for a path, building the FileSystemInfo the way the walk does.</summary>
    public static bool IsOpaqueWindowsLink(string linkPath) => IsOpaqueWindowsLink(InfoOf(linkPath));

    /// <summary>
    /// The one walker. <paramref name="lexicalDotDot"/> collapses `.`/`..` by string (Windows rule);
    /// otherwise they are components the walk applies itself, `..` popping the already-resolved
    /// <c>current</c> (Unix rule: the kernel applies `..` after the link before it is followed).
    /// <paramref name="canonical"/> is true when the OS placed the path (Windows): the result is then in the
    /// OS's spelling and may only be compared with <c>canonicalRealPath</c>, never with <see cref="RealPath"/>.
    /// </summary>
    private static string Walk(string path, bool lexicalDotDot, out bool unresolved, out bool canonical)
    {
        canonical = false;
        // Raw LinkTarget, not ResolveLinkTarget(returnFinalTarget: true) (2026-10-12): on Windows that
        // goes through GetFinalPathNameByHandle, which rewrites 8.3 names (RUNNER~1) on the resolved
        // side only, so a root and a target with no link between them would compare unequal.
        string full = lexicalDotDot
            ? IoPath.GetFullPath(path)
            : IoPath.IsPathRooted(path) ? path : IoPath.Combine(Environment.CurrentDirectory, path);
        string current = IoPath.GetPathRoot(full) ?? "";
        List<string> parts = Split(full, current);
        string? ownShare = ShareOf(full);
        int hops = 0;
        int index = 0;
        while (index < parts.Count)
        {
            string part = parts[index];
            if (!lexicalDotDot && part == ".")
            {
                index++;
                continue;
            }

            if (!lexicalDotDot && part == "..")
            {
                current = IoPath.GetDirectoryName(current) ?? current;
                index++;
                continue;
            }

            string next = IoPath.Combine(current, part);

            // #105: a network path that is not on the share the walk started on is not statted, not even to ask
            // whether it is a link: any touch authenticates to that host. The string is the answer: unresolved.
            if (IsForeignNetworkPath(next, ownShare))
            {
                unresolved = true;
                return IoPath.Combine([next, .. parts[(index + 1)..]]);
            }

            LinkRead link = ReadLink(next);

            // A string walk cannot place this and Windows follows it anywhere. The OS can say where the whole
            // typed path lands, not our half-spliced one (#100); when it cannot, the path stays unresolved and refused.
            if (link.Opaque)
            {
                // The raw target lost its first four characters (`\??\`); put them back to ask whether the OS would
                // open a network host to place it (#105).
                if (link.Target is { } raw && IsForeignNetworkPath(@"\??\" + raw, ownShare))
                {
                    unresolved = true;
                    return IoPath.Combine([raw, .. parts[(index + 1)..]]);
                }

                string? placed = PlaceByOs(full);
                canonical = placed is not null;
                unresolved = placed is null;

                // A refusal line reads `path → real`: the right side must show what the link points at (its raw
                // target), because the typed path there would claim the fence resolved it.
                return placed ?? IoPath.Combine([link.Target ?? next, .. parts[(index + 1)..]]);
            }

            if (link.Target is not { } target)
            {
                current = next;
                index++;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                unresolved = true;
                return IoPath.Combine([current, .. parts[index..]]);
            }

            // 2026-10-12: GetFullPath collapses `..` lexically, but POSIX applies it after the link is
            // followed. `.claude -> d/..` with `d -> ../../.claude/agents` walked to <repo> while the
            // kernel wrote to ~/.claude (found in review), so on Unix the target is spliced in raw.
            if (lexicalDotDot)
            {
                // The target may itself hold links, so the walk restarts from the top of the spliced path.
                string resolved = IoPath.GetFullPath(target, current);
                current = IoPath.GetPathRoot(resolved) ?? "";
                parts = [.. Split(resolved, current), .. parts[(index + 1)..]];
            }
            else
            {
                string targetRoot = IoPath.GetPathRoot(target) ?? "";
                if (targetRoot.Length > 0)
                    current = IoPath.GetFullPath(targetRoot, current);
                parts = [.. Split(target, targetRoot), .. parts[(index + 1)..]];
            }

            index = 0;
        }

        unresolved = false;
        return current;
    }

    /// <summary>True when the two paths are the same once normalized (case-insensitive on Windows).</summary>
    public static bool SamePath(string left, string right) =>
        Trimmed(IoPath.GetFullPath(left)).Equals(Trimmed(IoPath.GetFullPath(right)), Comparison);

    public bool IsInside(string resolvedPath) => IsUnder(resolvedPath, RealPath);

    /// <summary>
    /// True when this root is fenced and <paramref name="path"/> resolves outside it. A loop counts as
    /// escaping, and so is a Windows link the OS cannot place: a path that cannot be proved inside the root
    /// is not written in repo mode. On Windows the physical `..` reading is checked too, because the lexical
    /// one is reasoned, not measured.
    /// </summary>
    public bool Escapes(string path)
    {
        if (!Fenced)
            return false;

        return EscapesUnder(path, OperatingSystem.IsWindows())
            || (OperatingSystem.IsWindows() && EscapesUnder(path, lexicalDotDot: false));
    }

    private bool EscapesUnder(string path, bool lexicalDotDot)
    {
        string real = Walk(path, lexicalDotDot, out bool unresolved, out bool canonical);
        if (unresolved)
            return true;

        // Typed spellings meet RealPath and the OS's meet its own spelling of the root, never across: 8.3 vs long names.
        // No canonical root (the OS could not place it) cannot prove anything inside.
        return canonical
            ? canonicalRealPath.Value is not { } root || !IsUnder(real, root)
            : !IsInside(real);
    }

    private static bool IsUnder(string path, string root)
    {
        string candidate = Trimmed(path);
        string trimmedRoot = Trimmed(root);
        return candidate.Equals(trimmedRoot, Comparison)
            || candidate.StartsWith(trimmedRoot + IoPath.DirectorySeparatorChar, Comparison);
    }

    private static string? PlaceByOs(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        Interlocked.Increment(ref osPlacements);
        return WindowsFinalPath.TryResolve(path);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Trimmed(string path) =>
        path.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);

    private static List<string> Split(string fullPath, string pathRoot) =>
        [.. fullPath[pathRoot.Length..].Split(
            [IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)];

    private static FileSystemInfo InfoOf(string path) =>
        OperatingSystem.IsWindows() && Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    private static LinkRead ReadLink(string path)
    {
        FileSystemInfo info;
        string? target;
        try
        {
            // A directory link at the leaf is reported by FileInfo on Unix; Windows opens it differently.
            info = InfoOf(path);
            target = info.LinkTarget;
            if (target is null)
            {
                // 2026-10-13 (#101): a null LinkTarget is also a link whose reparse data is unreadable, or a reparse point that
                // is no link (cloud placeholder). A missing path reads (FileAttributes)(-1), every bit set: test it before ReparsePoint.
                FileAttributes attributes = info.Attributes;
                return new(null, attributes is not NoSuchEntry && attributes.HasFlag(FileAttributes.ReparsePoint));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An entry that cannot even be inspected may be a link: opaque, never "not a link".
            return new(null, true);
        }

        // Outside the try above: a real link whose structural read throws is opaque, never "not a link".
        return new(target, OperatingSystem.IsWindows() && (IsOpaqueWindowsLinkTarget(target) || IsOpaqueWindowsLink(info)));
    }

    /// <summary>One component's link state: its raw target when readable, and whether the string walk must not trust it.</summary>
    private readonly record struct LinkRead(string? Target, bool Opaque);
}
