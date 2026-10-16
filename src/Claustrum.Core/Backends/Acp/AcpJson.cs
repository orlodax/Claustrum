using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// The few typed reads every ACP reader makes of an incoming frame. Each one answers null for a missing property, a property of
// another kind, or a parent that is not an object, so no read of an agent's frame can throw on the pump thread.
internal static class AcpJson
{
    public static string? StringOf(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    public static JsonElement? ObjectOf(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Object } property ? property : null;

    public static int? IntOf(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt32(out int value) ? value : null;

    public static decimal? DecimalOf(JsonElement parent, string name) =>
        Property(parent, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetDecimal(out decimal value) ? value : null;

    private static JsonElement? Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement property) ? property : null;
}
