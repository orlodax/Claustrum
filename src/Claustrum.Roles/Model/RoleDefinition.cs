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
    string[] NonNegotiable);
