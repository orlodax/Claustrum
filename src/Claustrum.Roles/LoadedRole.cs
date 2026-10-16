using Claustrum.Roles.Model;

namespace Claustrum.Roles;

/// <summary>A role's metadata and raw ROLE.md template, plus whether a local `.claustrum/roles/` override contributed and what of it was ignored.</summary>
public sealed record LoadedRole(RoleDefinition Definition, string RoleMdTemplate, bool IsLocalOverride, string[]? Warnings = null);
