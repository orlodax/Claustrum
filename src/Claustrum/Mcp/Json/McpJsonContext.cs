using System.Text.Json.Serialization;
using Claustrum.Delegation;

namespace Claustrum.Mcp.Json;

// AGENTS.md "All JSON goes through a source-generated context" — MCP-only presentation DTOs
// (RoleSummary etc.), separate from Core's ClaustrumJsonContext and Casts' CastJsonContext because
// neither of those projects/namespaces knows about these types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(RoleSummary[]))]
[JsonSerializable(typeof(string[]))]
// Not a return type: `coordinate`'s `int[]? issues` parameter. The MCP SDK asks this resolver for a
// JsonTypeInfo per tool parameter while building the tool list, and a missing one takes the whole
// server down at startup, not just that tool — measured 2026-09-22 on the AOT binary:
// "JsonTypeInfo metadata for type 'System.Int32[]' was not provided", Hosting failed to start.
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DelegateAsyncResult))]
[JsonSerializable(typeof(JobStatusInfo))]
public sealed partial class McpJsonContext : JsonSerializerContext;
