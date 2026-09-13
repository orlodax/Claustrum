namespace Claustrum.Core.Model;

/// <summary>Harness-neutral capability level; each backend maps it to its own flags (docs/PLAN.md §A3).</summary>
public enum PermissionLevel
{
    ReadOnly,
    Edit,
    EditShell,
    Full,
}
