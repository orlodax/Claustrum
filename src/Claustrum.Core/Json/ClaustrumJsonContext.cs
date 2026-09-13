using System.Text.Json;
using System.Text.Json.Serialization;
using Claustrum.Core.Config;
using Claustrum.Core.Model;

namespace Claustrum.Core.Json;

// AGENTS.md "All JSON goes through ClaustrumJsonContext (source-generated)" — one context for
// every DTO in the app, no reflection-based serialization anywhere. snake_case properties and
// lower-snake enum strings are the emitted-JSON convention (docs/PLAN.md A2); ChangeKind opts out
// via its own [JsonConverter] because it is emitted as a single letter instead.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(ChangedFile))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(ClaustrumReport))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ConfigDocument))]
public sealed partial class ClaustrumJsonContext : JsonSerializerContext;
