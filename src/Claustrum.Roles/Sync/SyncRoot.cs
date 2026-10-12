using IoPath = System.IO.Path;

namespace Claustrum.Roles.Sync;

/// <summary>
/// The directory a sync writes under and, in repo mode, the fence that keeps it there: a target whose
/// real path (every link on the way resolved) lies outside <see cref="RealPath"/>, or cannot be resolved
/// (a link loop, an NT-namespace target on Windows), is refused. Under `--global` the root is the user's
/// own and unfenced. NOTES.md "`sync` never writes outside its root through a linked directory" holds
/// the rules and the Windows notes (issues #95, #93).
/// </summary>
public sealed class SyncRoot
{
    private const int MaxLinkHops = 40;

    private SyncRoot(string path, bool fenced)
    {
        Path = path;
        RealPath = Resolve(path);
        Fenced = fenced;
    }

    /// <summary>The root as given: the cwd in repo mode, the global root under `--global`.</summary>
    public string Path { get; }

    public string RealPath { get; }

    public bool Fenced { get; }

    public static SyncRoot Repo(string cwd) => new(cwd, fenced: true);

    public static SyncRoot Global(string root) => new(root, fenced: false);

    public static string Resolve(string path) => Resolve(path, out _);

    /// <summary>
    /// The real path of <paramref name="path"/>: every link on the way, directories and leaf alike, is
    /// followed; trailing components that do not exist yet stay as typed. <paramref name="unresolved"/> is
    /// true when the walk could not be finished: the 40-hop cap was hit, or on Windows a link target is
    /// opaque (<see cref="IsOpaqueWindowsLink(FileSystemInfo)"/>); the result is then the path as far as it was resolved.
    /// `..` follows the OS: physical on Unix, lexical on Windows (NOTES.md, same section).
    /// </summary>
    public static string Resolve(string path, out bool unresolved) => Walk(path, OperatingSystem.IsWindows(), out unresolved);

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
    /// absolute one only `\??\`-stripped, and ToString() keeps that string (FullName would re-root it): a
    /// result that is not fully qualified is an NT-namespace target, whatever its first component (#95 review).
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A link that cannot be read is not "not a link": fail closed.
            return true;
        }
    }

    /// <summary>The same check for a path, building the FileSystemInfo the way the walk does.</summary>
    public static bool IsOpaqueWindowsLink(string linkPath) => IsOpaqueWindowsLink(InfoOf(linkPath));

    /// <summary>
    /// The one walker. <paramref name="lexicalDotDot"/> collapses `.`/`..` by string (Windows rule);
    /// otherwise they are components the walk applies itself, `..` popping the already-resolved
    /// <c>current</c> (Unix rule: the kernel applies `..` after the link before it is followed).
    /// </summary>
    private static string Walk(string path, bool lexicalDotDot, out bool unresolved)
    {
        // Raw LinkTarget, not ResolveLinkTarget(returnFinalTarget: true) (2026-10-12): on Windows that
        // goes through GetFinalPathNameByHandle, which rewrites 8.3 names (RUNNER~1) on the resolved
        // side only, so a root and a target with no link between them would compare unequal.
        string full = lexicalDotDot
            ? IoPath.GetFullPath(path)
            : IoPath.IsPathRooted(path) ? path : IoPath.Combine(Environment.CurrentDirectory, path);
        string current = IoPath.GetPathRoot(full) ?? "";
        List<string> parts = Split(full, current);
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
            string? target = LinkTargetOf(next, out bool opaque);
            if (target is null)
            {
                current = next;
                index++;
                continue;
            }

            // An absolute NT-namespace target cannot be placed by a string walk, and Windows follows it anywhere: refuse.
            if (opaque)
            {
                unresolved = true;
                return IoPath.Combine([target, .. parts[(index + 1)..]]);
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

    public bool IsInside(string resolvedPath)
    {
        string candidate = Trimmed(resolvedPath);
        string root = Trimmed(RealPath);
        return candidate.Equals(root, Comparison)
            || candidate.StartsWith(root + IoPath.DirectorySeparatorChar, Comparison);
    }

    /// <summary>
    /// True when this root is fenced and <paramref name="path"/> resolves outside it. A loop counts as
    /// escaping, and so is an opaque Windows target: a path that cannot be proved inside the root is not
    /// written in repo mode. On Windows the physical `..` reading is checked too, because the lexical
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
        string real = Walk(path, lexicalDotDot, out bool unresolved);
        return unresolved || !IsInside(real);
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

    private static string? LinkTargetOf(string path, out bool opaque)
    {
        opaque = false;
        FileSystemInfo info;
        string? target;
        try
        {
            // A directory link at the leaf is reported by FileInfo on Unix; Windows opens it differently.
            info = InfoOf(path);
            target = info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Outside the try above: a real link whose structural read throws is opaque, never "not a link".
        opaque = target is not null && OperatingSystem.IsWindows() && (IsOpaqueWindowsLinkTarget(target) || IsOpaqueWindowsLink(info));
        return target;
    }
}
