using Claustrum.Core.Model;

namespace Claustrum.Core.Tracing;

// #145: the tool and permission names the traced backends use, folded into families: claude's `Bash`/`Edit`/`WebFetch`, opencode's
// `bash`/`edit`/`webfetch` and its permission names (`shell`, `edit`, `read`), and ACP's tool `kind`s (`execute`, `search`, `fetch`).
internal static class ToolNames
{
    public static ToolFamily FamilyOf(string? name) => name?.ToLowerInvariant() switch
    {
        "bash" or "shell" or "execute" or "terminal" or "powershell" => ToolFamily.Shell,
        "read" or "view" => ToolFamily.Read,
        "glob" or "grep" or "list" or "ls" or "search" or "codesearch" => ToolFamily.Search,
        "edit" or "write" or "multiedit" or "notebookedit" or "patch" or "apply_patch" or "delete" or "move" => ToolFamily.Edit,
        "webfetch" or "websearch" or "fetch" => ToolFamily.Fetch,
        _ => ToolFamily.Other,
    };

    /// <summary>
    /// Whether the permission level takes this family away on purpose, so a refusal or a missing tool there is the policy working: edits
    /// below `edit`, the shell at `edit` (claude's `--disallowedTools Bash`, opencode's `bash: deny`). A readonly role keeps the shell it
    /// reads a diff with (claude's `git diff`/`gh issue` rules), so a shell it lacks there counts.
    /// </summary>
    public static bool Withheld(ToolFamily family, PermissionLevel level) => family switch
    {
        ToolFamily.Edit => level is PermissionLevel.ReadOnly or PermissionLevel.Shell,
        ToolFamily.Shell => level is PermissionLevel.Edit,
        _ => false,
    };
}
