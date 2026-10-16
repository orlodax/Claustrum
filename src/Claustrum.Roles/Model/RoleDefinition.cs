namespace Claustrum.Roles.Model;

/// <summary>
/// Deserialized `roles/&lt;role&gt;/role.json` (docs/PLAN.md §B2), after any local-override deep merge.
/// <paramref name="Primary"/> marks a role meant to be picked as the main agent and orchestrate others
/// (the architect); only opencode's `mode` reads it today (#85, NOTES.md "A primary role").
/// <paramref name="UserMcp"/> is the role's default for keeping the operator's user-level MCP servers on claude
/// (the browser roles need theirs); `roles.&lt;role&gt;.user_mcp` in the user config overrides it (#127, NOTES.md "A claude role gets the repo's MCP servers, not the operator's").
/// </summary>
public sealed record RoleDefinition(
    string Name,
    string Description,
    string Color,
    bool Blind,
    Dictionary<string, RoleTier> Tiers,
    string Permission,
    string[] Deny,
    string[] MayDelegate,
    string Report,
    string[] Harnesses,
    string[] NonNegotiable,
    string[]? Tools = null,
    string[]? WithoutTools = null,
    bool Primary = false,
    bool UserMcp = false)
{
    /// <summary>
    /// Tool names this role needs on top of what `permission` grants — the browser MCP servers, or
    /// `Write` for a role whose whole output is a file. Optional in role.json, hence the null-safe
    /// projection: a role that names none behaves exactly as before (#29).
    /// </summary>
    public string[] ExtraTools => Tools ?? [];

    /// <summary>
    /// Tool names `permission` grants that this role has no use for — the demo-author's
    /// `NotebookEdit` on `edit+shell` (PR #42 review). Optional, like <see cref="Tools"/>.
    /// </summary>
    public string[] DroppedTools => WithoutTools ?? [];
}
