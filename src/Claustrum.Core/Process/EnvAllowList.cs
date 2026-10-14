using Claustrum.Core.Jobs;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Process;

// docs/PLAN.md A4's list, plus PATHEXT/windir/SystemDrive/ProgramFiles/ProgramFiles(x86)/ProgramData
// (NOTES.md "Env allow-list is dead" — a Windows child needs these to resolve its own PATH/PATHEXT
// and %ProgramFiles% lookups). `*` entries are prefix/suffix wildcards on the variable NAME only —
// values are never inspected or logged. `passthroughAll: true` (config `env_passthrough: "all"`)
// disables filtering entirely; caller-supplied `--env` always wins regardless of the list.
public static class EnvAllowList
{
    private static readonly string[] exactNames =
    [
        "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
        "SystemRoot", "ComSpec", "LANG", "SHELL", "TERM", "GH_TOKEN", "GITHUB_TOKEN",
        "PATHEXT", "windir", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramData",
        // #57: Claude Code's home (login, CLAUDE.md, agents), so a delegated claude reads the caller's.
        // By exact name: a CLAUDE_ prefix would also forward a parent session's CLAUDE_CODE_* markers.
        "CLAUDE_CONFIG_DIR",
        // macOS's per-user temp directory; without it a child falls back to the shared /tmp (2026-10-14).
        "TMPDIR",
    ];

    // ⚠ Deliberately no CLAUSTRUM_ prefix (2026-09-21), and `passthroughAll` drops the tree variable
    // all the same (2026-09-22): membership in a job tree is handed out by a coordinator, never
    // forwarded by a member, which holds its §D4 reservation for its whole lifetime — a `claustrum
    // run` that inherited its parent's CLAUSTRUM_PARENT_JOB would be refused by the ledger
    // (measured: `$0.00 remaining`). `coordinate` hands it down deliberately, on its architect's
    // request env, where caller-supplied values win over both rules (NOTES.md "Tree budget
    // accounting is a file ledger", "coordinate: a spawned architect…").
    // DOTNET_ROOT (and DOTNET_ROOT_ARM64, _X64, (x86)): a framework-dependent `claustrum` (the dotnet tool) that an agent
    // runs from inside a backend cannot find a runtime outside the default location without it, and exits printing only an
    // error (PR #116, first macOS CI run, 2026-10-14: a nested `run builder` under a coordinate architect).
    private static readonly string[] prefixes =
        ["XDG_", "ANTHROPIC_", "OPENROUTER_", "OPENCODE_", "CURSOR_", "COPILOT_", "NODE_", "DOTNET_ROOT"];

    private const string ApiKeySuffix = "_API_KEY";

    public static Dictionary<string, string> Build(IPlatform platform, IReadOnlyDictionary<string, string> callerEnv, bool passthroughAll)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in platform.GetEnvironmentVariables())
            if (passthroughAll ? !IsTreeMembership(entry.Key) : IsAllowed(entry.Key))
                result[entry.Key] = entry.Value;

        foreach (KeyValuePair<string, string> entry in callerEnv)
            result[entry.Key] = entry.Value;

        return result;
    }

    // `env_passthrough: "all"` turns off the allow-list, not the rule above it: an inherited tree id
    // would enrol a grandchild in a tree whose remainder its own parent already holds reserved.
    private static bool IsTreeMembership(string name) =>
        name.Equals(BudgetLedger.TreeVariable, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowed(string name) =>
        exactNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.EndsWith(ApiKeySuffix, StringComparison.OrdinalIgnoreCase)
        || prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
