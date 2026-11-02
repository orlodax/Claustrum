using System.Text.RegularExpressions;

namespace Claustrum.Core.Tracing;

// #145: paths and URLs as EnvironmentClassifier compares them, by string alone: a path is made absolute against the job's cwd, `/`-separated,
// `.` and `..` resolved, with no trailing separator, and compared case-insensitively on Windows only. Nothing here touches the filesystem,
// so a recorded log from another machine classifies the same.
internal static partial class TracePath
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string path, string cwd)
    {
        string slashed = path.Trim().Replace('\\', '/');
        if (!IsAbsolute(slashed))
            slashed = $"{cwd.Trim().Replace('\\', '/').TrimEnd('/')}/{slashed}";
        if (!IsAbsolute(slashed))
            return slashed.TrimEnd('/');

        string root = slashed.StartsWith("//", StringComparison.Ordinal) ? "//" : slashed.StartsWith('/') ? "/" : slashed[..3];
        List<string> parts = [];
        foreach (string part in slashed[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return root + string.Join('/', parts);
    }

    public static bool Same(string path, string other) => string.Equals(path, other, Comparison);

    /// <summary>Whether a normalized <paramref name="path"/> is <paramref name="directory"/> or below it.</summary>
    public static bool IsUnder(string path, string directory) =>
        Same(path, directory) || path.StartsWith(directory.EndsWith('/') ? directory : $"{directory}/", Comparison);

    /// <summary>The absolute paths written in a command or an error line, without the punctuation that ends a message (`cd: /x: No such…`).</summary>
    public static IEnumerable<string> AbsolutePaths(string text) =>
        AbsolutePathPattern().Matches(text).Select(match => match.Value.TrimEnd(':', '.', ',')).Where(path => path.Length > 1);

    public static IEnumerable<Uri> Urls(string text) =>
        UrlPattern().Matches(text)
            .Select(match => Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ':', ')', '\''), UriKind.Absolute, out Uri? url) ? url : null)
            .OfType<Uri>();

    // The app under test, not the environment: a ui-reviewer's `curl` against its own dev server answers 403 on purpose.
    public static bool IsLoopback(Uri url) =>
        url.IsLoopback || url.Host is "0.0.0.0" or "[::]" || url.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    public static bool IsGitHub(Uri url) => url.Host.ToLowerInvariant() is "github.com" or "www.github.com" or "api.github.com" or "raw.githubusercontent.com";

    private static bool IsAbsolute(string slashed) =>
        slashed.StartsWith('/') || (slashed.Length >= 3 && char.IsAsciiLetter(slashed[0]) && slashed[1] == ':' && slashed[2] == '/');

    // Not after a word character, `:` or `/` (so neither `$HOME/x` nor a URL's `//host/path`); a drive path on Windows.
    [GeneratedRegex("""(?<![\w.~:/\\$-])(?:/[^\s'"`;|&<>(){}\[\],]+|[A-Za-z]:[\\/][^\s'"`;|&<>(){}\[\],]*)""")]
    private static partial Regex AbsolutePathPattern();

    [GeneratedRegex("""https?://[^\s'"`<>(){}\[\]]+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
