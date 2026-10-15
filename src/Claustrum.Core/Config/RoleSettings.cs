namespace Claustrum.Core.Config;

// The "roles.<name>" section of claustrum.json (docs/PLAN.md A7) — a user override of one role's
// built-in defaults from role.json. Deny here is concatenated onto the role's own deny list.
// UserMcp (#128): true lets a claude-backed role keep the operator's user-level MCP servers; unset means no.
public sealed record RoleSettings(string? Model, string? Effort, string? Permission, string[]? Deny, bool? UserMcp = null);
