namespace Claustrum.Core.Config;

// The "roles.<name>" section of claustrum.json (docs/PLAN.md A7) — a user override of one role's
// built-in defaults from role.json. Deny here is concatenated onto the role's own deny list.
public sealed record RoleSettings(string? Model, string? Effort, string? Permission, string[]? Deny);
