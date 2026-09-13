using System.Text.RegularExpressions;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

public sealed record NpmShimTarget(string Executable, string[] PrefixArgs);

// npm's cmd-shim generator produces exactly two shapes on Windows (verified against a real
// `claude.cmd` and `yo.cmd` install, 2026-09-13): a shim pointing straight at a bundled .exe
// (compiled binaries), or the classic `"%_prog%" "<script>" %*` form for pure-JS CLIs, where
// `_prog` is `%dp0%\node.exe` if bundled, else the bare `node` found on PATH. NOTES.md "npm shims
// on Windows" — both are run directly because CreateProcess ignores PATHEXT and cmd.exe mangles
// `%` and long argument lines.
public static partial class NpmShimParser
{
    public static NpmShimTarget? TryParse(string shimContent, string shimDirectory, IPlatform platform)
    {
        Match exeMatch = ExePattern().Match(shimContent);
        if (exeMatch.Success)
            return new NpmShimTarget(ResolveDp0(exeMatch.Groups["path"].Value, shimDirectory), []);

        Match scriptMatch = ScriptPattern().Match(shimContent);
        if (!scriptMatch.Success)
            return null;

        string scriptPath = ResolveDp0(scriptMatch.Groups["path"].Value, shimDirectory);
        string bundledNode = Path.Combine(shimDirectory, "node.exe");
        string node = platform.FileExists(bundledNode) ? bundledNode : "node";
        return new NpmShimTarget(node, [scriptPath]);
    }

    private static string ResolveDp0(string rawPath, string shimDirectory) =>
        rawPath.Replace("%dp0%", shimDirectory, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""("(?<path>[^"\r\n]*node_modules[^"\r\n]*\.exe)")\s+%\*""", RegexOptions.IgnoreCase)]
    private static partial Regex ExePattern();

    [GeneratedRegex("""("(?<path>[^"\r\n]*node_modules[^"\r\n]*)")\s+%\*""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();
}
