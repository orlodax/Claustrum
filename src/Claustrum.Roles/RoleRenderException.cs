namespace Claustrum.Roles;

/// <summary>An unknown `{{token}}`, a missing tier, or a missing part file — see docs/PLAN.md §B2.</summary>
public sealed class RoleRenderException(string message) : Exception(message);
