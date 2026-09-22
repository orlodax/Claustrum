namespace Claustrum.Core.Backends;

// `Advisories` is deliberately not a `Problems` entry: a problem makes `BackendsCommands`
// skip `--probe`'s paid round trip, and a note that blocks nothing must not do that
// (NOTES.md "doctor advisories are not problems"). Nullable-with-default so the many
// `new Doctor(found, path, version, problems)` call sites stay untouched; readers get `[]`.
public sealed record Doctor(bool Found, string? Path, string? Version, string[] Problems, string[]? Advisories = null)
{
    public string[] Advisories { get; init; } = Advisories ?? [];
}
