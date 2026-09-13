using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

// Exact allow-list from docs/PLAN.md A4; `*` entries are prefix/suffix wildcards on the variable
// NAME only — values are never inspected or logged. `env_passthrough: all` disables filtering
// entirely; caller-supplied `--env` always wins regardless of the list.
public static class EnvAllowList
{
    private static readonly string[] exactNames =
    [
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
        "SystemRoot", "ComSpec", "LANG", "SHELL", "TERM", "GH_TOKEN", "GITHUB_TOKEN",
    ];

    private static readonly string[] prefixes =
        ["XDG_", "ANTHROPIC_", "OPENROUTER_", "OPENCODE_", "CURSOR_", "COPILOT_", "NODE_"];

    private const string ApiKeySuffix = "_API_KEY";

    public static Dictionary<string, string> Build(IPlatform platform, IReadOnlyDictionary<string, string> callerEnv, bool passthroughAll)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in platform.GetEnvironmentVariables())
            if (passthroughAll || IsAllowed(entry.Key))
                result[entry.Key] = entry.Value;

        foreach (KeyValuePair<string, string> entry in callerEnv)
            result[entry.Key] = entry.Value;

        return result;
    }

    private static bool IsAllowed(string name) =>
        exactNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.EndsWith(ApiKeySuffix, StringComparison.OrdinalIgnoreCase)
        || prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
