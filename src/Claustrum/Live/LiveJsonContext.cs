using System.Text.Json.Serialization;

namespace Claustrum.Live;

// AGENTS.md "All JSON goes through a source-generated context": the CLI-side documents of the live channel
// (`jobs wait --json`), compact because stdout carries exactly one. snake_case like every emitted document.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JobWaitResult))]
internal sealed partial class LiveJsonContext : JsonSerializerContext;
