using System.Text.Json.Serialization;

namespace Claustrum.Mcp.Json;

// AGENTS.md "All JSON goes through a source-generated context" — MCP-only presentation DTOs
// (RoleSummary etc.), separate from Core's ClaustrumJsonContext and Casts' CastJsonContext because
// neither of those projects/namespaces knows about these types.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(RoleSummary[]))]
public sealed partial class McpJsonContext : JsonSerializerContext;
