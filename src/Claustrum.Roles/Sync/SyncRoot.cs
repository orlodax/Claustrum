using IoPath = System.IO.Path;

namespace Claustrum.Roles.Sync;

/// <summary>
/// The directory a sync writes under and, in repo mode, the fence that keeps it there: a target whose
/// real path (every link on the way resolved) lies outside <see cref="RealPath"/> is refused. Under
/// `--global` the root is the user's own and unfenced. NOTES.md "`sync` never writes outside its root
/// through a linked directory" holds the rules and the Windows notes (issues #95, #93).
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
    /// followed; trailing components that do not exist yet stay as typed. <paramref name="looped"/> is
    /// true when the 40-hop cap was hit, and the result is then the path as far as it was resolved.
    /// `..` follows the OS: physical on Unix, lexical on Windows (NOTES.md, same section).
    /// </summary>
    public static string Resolve(string path, out bool looped) => Walk(path, OperatingSystem.IsWindows(), out looped);

    /// <summary>
    /// The one walker. <paramref name="lexicalDotDot"/> collapses `.`/`..` by string (Windows rule);
    /// otherwise they are components the walk applies itself, `..` popping the already-resolved
    /// <c>current</c> (Unix rule: the kernel applies `..` after the link before it is followed).
    /// </summary>
    private static string Walk(string path, bool lexicalDotDot, out bool looped)
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
            string? target = LinkTargetOf(next);
            if (target is null)
            {
                current = next;
                index++;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                looped = true;
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

        looped = false;
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
    /// escaping: a path that cannot be proved inside the root is not written in repo mode. On Windows
    /// the physical `..` reading is checked too, because the lexical one is reasoned, not measured.
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
        string real = Walk(path, lexicalDotDot, out bool looped);
        return looped || !IsInside(real);
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Trimmed(string path) =>
        path.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar);

    private static List<string> Split(string fullPath, string pathRoot) =>
        [.. fullPath[pathRoot.Length..].Split(
            [IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)];

    private static string? LinkTargetOf(string path)
    {
        try
        {
            // A directory link at the leaf is reported by FileInfo on Unix; Windows opens it differently.
            FileSystemInfo info = OperatingSystem.IsWindows() && Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
