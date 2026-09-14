using System.Text.Json.Serialization;

namespace Claustrum.Casts;

// AGENTS.md "All JSON goes through a source-generated context, no reflection-based serialization" —
// separate from Core's ClaustrumJsonContext because Cast is not a Core type (Cast.cs).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(Cast))]
[JsonSerializable(typeof(CastQuestionnaireResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class CastJsonContext : JsonSerializerContext;
