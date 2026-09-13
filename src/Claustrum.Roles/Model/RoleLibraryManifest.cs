namespace Claustrum.Roles.Model;

/// <summary>Root of `roles/library.json` (docs/PLAN.md §B2) — the model classes and tiers every role.json draws from.</summary>
public sealed record RoleLibraryManifest(string Version, string[] Classes, string[] Tiers);
