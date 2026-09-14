using System.Text.Json.Serialization;
using Claustrum.Delegation;

namespace Claustrum.Mcp.Json;

// AGENTS.md "All JSON goes through a source-generated context" — MCP-only presentation DTOs
// (RoleSummary etc.), separate from Core's ClaustrumJsonContext and Casts' CastJsonContext because
// neither of those projects/namespaces knows about these types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(RoleSummary[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DelegateAsyncResult))]
[JsonSerializable(typeof(JobStatusInfo))]
public sealed partial class McpJsonContext : JsonSerializerContext;
