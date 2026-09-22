namespace Claustrum.Mcp.Json;

// `Advisories` mirrors `Doctor.Advisories`: notes that do not block a run and, unlike `Problems`,
// never suppress `--probe` (NOTES.md "doctor advisories are not problems").
public sealed record BackendDoctorEntry(string Name, bool Found, string? Path, string? Version, string[] Problems, string[] Advisories);
