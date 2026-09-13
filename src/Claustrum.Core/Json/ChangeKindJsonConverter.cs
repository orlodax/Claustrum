using System.Text.Json;
using System.Text.Json.Serialization;
using Claustrum.Core.Model;

namespace Claustrum.Core.Json;

public sealed class ChangeKindJsonConverter : JsonConverter<ChangeKind>
{
    public override ChangeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "A" => ChangeKind.Added,
            "M" => ChangeKind.Modified,
            "D" => ChangeKind.Deleted,
            string other => throw new JsonException($"unknown change kind '{other}'"),
            null => throw new JsonException("change kind must not be null"),
        };

    public override void Write(Utf8JsonWriter writer, ChangeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ChangeKind.Added => "A",
            ChangeKind.Modified => "M",
            ChangeKind.Deleted => "D",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        });
}
