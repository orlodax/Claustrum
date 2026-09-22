using System.Text.Json.Serialization;

namespace Claustrum.Coordination;

// AGENTS.md "All JSON goes through a source-generated context, no reflection" — this one is for
// JSON this repo *reads from another tool* rather than writes: gh's `--json` output. Camel case is
// gh's own shape (`number`, `title`, `labels[].name`), not this repo's snake_case contract.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GhIssueDocument))]
internal sealed partial class CoordinationJsonContext : JsonSerializerContext;
