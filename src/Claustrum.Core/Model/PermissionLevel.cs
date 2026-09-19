using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

/// <summary>How much a backend may do to the working tree and the shell for one run.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PermissionLevel>))]
public enum PermissionLevel
{
    ReadOnly,

    // Read the tree, run commands, change nothing. The level a review role that has to *operate* the
    // code needs — ui-reviewer starts a dev server and drives a browser but is forbidden to edit —
    // which ReadOnly cannot express (it withholds the shell) and EditShell over-grants (review
    // finding: ui-reviewer shipped as edit+shell purely because this rung did not exist).
    Shell,

    Edit,
    EditShell,
    Full,
}
