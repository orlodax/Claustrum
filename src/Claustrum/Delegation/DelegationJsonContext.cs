using System.Text.Json.Serialization;

namespace Claustrum.Delegation;

// AGENTS.md "All JSON goes through a source-generated context": the request a detached host (`jobs host`, #124) reads back
// out of the job directory. snake_case like every document Claustrum writes.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DelegateRequest))]
internal sealed partial class DelegationJsonContext : JsonSerializerContext;
