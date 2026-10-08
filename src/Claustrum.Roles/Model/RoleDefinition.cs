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
    string[]? Tools = null,
    string[]? WithoutTools = null)
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

    /// <summary>Whether <paramref name="tier"/> runs the same model class as `high`, so a stub may say "identical model".</summary>
    public bool KeepsModelAt(string tier) => Tiers[tier].Model == Tiers["high"].Model;
}
