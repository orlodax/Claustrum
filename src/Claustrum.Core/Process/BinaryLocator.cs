using Claustrum.Core.Config;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

public sealed record ResolvedBinary(string Executable, string[] Args);

// PATH + PATHEXT search, `backends.<name>.path` override, and the Windows npm-shim unwrap
// (docs/PLAN.md A4). Everything here reads through IPlatform so the Windows branch is exercised
// by tests on Linux too.
public static class BinaryLocator
{
    public static ResolvedBinary Locate(string name, string[] args, BackendConfig? config, IPlatform platform)
    {
        string candidate = ResolveCandidate(name, config, platform) ?? throw new BackendNotFoundException(name);

        if (platform.Os != ClaustrumOs.Windows || !candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return new ResolvedBinary(candidate, args);

        string shimDirectory = Path.GetDirectoryName(candidate) ?? "";
        NpmShimTarget? target = NpmShimParser.TryParse(platform.ReadAllText(candidate), shimDirectory, platform);

        // An unrecognized shim shape runs the .cmd directly on ArgumentList rather than through a
        // composed `cmd /d /s /c` command line — NOTES.md "npm shims on Windows" has the measured
        // failure modes of the cmd.exe composition this replaced.
        return target is not null
            ? new ResolvedBinary(target.Executable, [.. target.PrefixArgs, .. args])
            : new ResolvedBinary(candidate, args);
    }

    private static string? ResolveCandidate(string name, BackendConfig? config, IPlatform platform)
    {
        if (config?.Path is { Length: > 0 } explicitPath && platform.FileExists(explicitPath))
            return explicitPath;

        string[] extensions = platform.GetPathExtensions() is { Length: > 0 } fromEnv ? fromEnv : [""];

        foreach (string directory in platform.GetPathEntries())
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (platform.FileExists(candidate))
                    return candidate;
            }

        return null;
    }
}
