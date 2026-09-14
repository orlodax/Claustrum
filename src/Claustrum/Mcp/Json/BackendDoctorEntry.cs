namespace Claustrum.Mcp.Json;

public sealed record BackendDoctorEntry(string Name, bool Found, string? Path, string? Version, string[] Problems);
