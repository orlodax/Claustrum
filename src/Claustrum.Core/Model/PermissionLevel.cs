namespace Claustrum.Core.Model;

/// <summary>How much a backend may do to the working tree and the shell for one run.</summary>
public enum PermissionLevel
{
    ReadOnly,
    Edit,
    EditShell,
    Full,
}
