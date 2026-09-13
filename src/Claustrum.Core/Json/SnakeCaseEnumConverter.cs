using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Claustrum.Core.Json;

// JsonSourceGenerationOptions.UseStringEnumConverter does NOT apply PropertyNamingPolicy to enum
// member names — verified empirically 2026-09-13 (RunStatus.BackendMissing serialized as
// "BackendMissing", not "backend_missing", the emitted-JSON contract in docs/PLAN.md A2). This
// generic converter does the PascalCase -> snake_case conversion explicitly and is attached per
// enum via [JsonConverter(typeof(SnakeCaseEnumConverter<TEnum>))] rather than relied on globally,
// so a future enum without the attribute fails loudly (serializes as its raw int) instead of
// silently emitting the wrong casing.
public sealed class SnakeCaseEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> toSnakeCase = BuildToSnakeCase();
    private static readonly Dictionary<string, TEnum> fromSnakeCase = toSnakeCase.ToDictionary(pair => pair.Value, pair => pair.Key);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (value is not null && fromSnakeCase.TryGetValue(value, out TEnum result))
            return result;

        throw new JsonException($"unknown {typeof(TEnum).Name} value '{value}'");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(toSnakeCase[value]);

    private static Dictionary<TEnum, string> BuildToSnakeCase()
    {
        Dictionary<TEnum, string> map = [];
        foreach (TEnum value in Enum.GetValues<TEnum>())
            map[value] = ToSnakeCase(value.ToString());

        return map;
    }

    private static string ToSnakeCase(string pascalCase)
    {
        StringBuilder builder = new();
        for (int i = 0; i < pascalCase.Length; i++)
        {
            char c = pascalCase[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
