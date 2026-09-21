using System.Text.Json;
using System.Text.Json.Serialization;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;

namespace Claustrum.Core.Json;

// AGENTS.md "All JSON goes through ClaustrumJsonContext (source-generated)" — one context for
// every DTO in the app, no reflection-based serialization anywhere. snake_case properties are the
// emitted-JSON convention (docs/PLAN.md A2). Enums are NOT covered by UseStringEnumConverter: it
// does not apply PropertyNamingPolicy to enum member names (verified 2026-09-13 — it would emit
// "BackendMissing", not "backend_missing"), so every enum instead carries its own
// [JsonConverter(typeof(SnakeCaseEnumConverter<T>))] (or, for ChangeKind, a single-letter A/M/D
// converter) directly on the enum declaration.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(ChangedFile))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(ClaustrumReport))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(BudgetLedgerEntry))]
public sealed partial class ClaustrumJsonContext : JsonSerializerContext;
