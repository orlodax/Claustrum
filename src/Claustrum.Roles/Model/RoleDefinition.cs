namespace Claustrum.Roles.Model;

/// <summary>Deserialized `roles/&lt;role&gt;/role.json` (docs/PLAN.md §B2), after any local-override deep merge.</summary>
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
    string[]? Tools = null)
{
    /// <summary>
    /// Tool names this role needs on top of what `permission` grants — the browser MCP servers, or
    /// `Write` for a role whose whole output is a file. Optional in role.json, hence the null-safe
    /// projection: a role that names none behaves exactly as before (#29).
    /// </summary>
    public string[] ExtraTools => Tools ?? [];
}
