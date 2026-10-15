using System.Text.Json.Serialization;

namespace Claustrum.Live;

// AGENTS.md "All JSON goes through a source-generated context": the CLI-side documents of the live channel
// (`jobs wait --json`, `run --detach --json`, a tree's needs_input), compact because stdout carries exactly one, and the
// detached host's record. snake_case like every emitted document.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JobWaitResult))]
[JsonSerializable(typeof(StartedResult))]
[JsonSerializable(typeof(NeedsInputResult))]
[JsonSerializable(typeof(HostRecord))]
[JsonSerializable(typeof(OwnerRecord))]
internal sealed partial class LiveJsonContext : JsonSerializerContext;
