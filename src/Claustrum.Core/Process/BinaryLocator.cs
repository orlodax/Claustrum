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
        if (target is not null)
            return new ResolvedBinary(target.Executable, [.. target.PrefixArgs, .. args]);

        string commandLine = CmdEscaping.BuildCommandLine([candidate, .. args]);
        return new ResolvedBinary("cmd.exe", ["/d", "/s", "/c", commandLine]);
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
