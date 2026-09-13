namespace Claustrum.Core.Model;

/// <summary>Everything the runner needs about a role after config resolution; Roles → Core seam.</summary>
public sealed record ResolvedRole(
    string Name,
    string SystemPrompt,
    string Backend,
    string Model,
    string Effort,
    PermissionPolicy Permission,
    bool Blind);
