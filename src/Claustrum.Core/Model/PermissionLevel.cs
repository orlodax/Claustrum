using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Model;

/// <summary>How much a backend may do to the working tree and the shell for one run.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<PermissionLevel>))]
public enum PermissionLevel
{
    ReadOnly,
    Edit,
    EditShell,
    Full,
}
