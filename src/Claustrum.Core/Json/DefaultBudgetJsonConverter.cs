using System.Text.Json;
using System.Text.Json.Serialization;
using Claustrum.Core.Config;

namespace Claustrum.Core.Json;

// #137: HandleNull, or `"budget_usd": null` never reaches Read and lands as the absent key's null, bringing the $5 back.
public sealed class DefaultBudgetJsonConverter : JsonConverter<DefaultBudget?>
{
    public override bool HandleNull => true;

    public override DefaultBudget? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Null => new DefaultBudget(null),
        JsonTokenType.Number => new DefaultBudget(reader.GetDecimal()),
        _ => throw new JsonException("defaults.budget_usd must be a number of USD, or null for no default cap"),
    };

    public override void Write(Utf8JsonWriter writer, DefaultBudget? value, JsonSerializerOptions options)
    {
        if (value?.Usd is { } usd)
            writer.WriteNumberValue(usd);
        else
            writer.WriteNullValue();
    }
}
