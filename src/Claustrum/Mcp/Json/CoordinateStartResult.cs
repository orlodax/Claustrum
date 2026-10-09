namespace Claustrum.Mcp.Json;

// `coordinate`'s answer: delegate_async's pair plus what the plan warned about before the run (#74 G1).
public sealed record CoordinateStartResult(string JobId, string LogPath, string[] Warnings);
