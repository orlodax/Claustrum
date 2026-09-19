using Claustrum.Core.Model;

namespace Claustrum.Core.Config;

// CLI/config string forms only ("readonly|edit|edit+shell|full", docs/PLAN.md A7) — deliberately
// distinct from PermissionLevel's own JSON wire form, which goes through the context-wide
// snake_case string-enum converter (e.g. "edit_shell") when a RunRequest is persisted to disk.
public static class PermissionLevelParser
{
    public static bool TryParse(string value, out PermissionLevel level)
    {
        switch (value)
        {
            case "readonly":
                level = PermissionLevel.ReadOnly;
                return true;
            case "shell":
                level = PermissionLevel.Shell;
                return true;
            case "edit":
                level = PermissionLevel.Edit;
                return true;
            case "edit+shell":
                level = PermissionLevel.EditShell;
                return true;
            case "full":
                level = PermissionLevel.Full;
                return true;
            default:
                level = default;
                return false;
        }
    }

    public static string ToConfigString(PermissionLevel level) => level switch
    {
        PermissionLevel.ReadOnly => "readonly",
        PermissionLevel.Shell => "shell",
        PermissionLevel.Edit => "edit",
        PermissionLevel.EditShell => "edit+shell",
        PermissionLevel.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
