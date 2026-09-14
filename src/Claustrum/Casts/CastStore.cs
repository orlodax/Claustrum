using System.Text.Json;

namespace Claustrum.Casts;

// `.claustrum/casts/<name>.json` (docs/PLAN.md §D1). Mirrors JobDirectory/ClaudeSync: plain
// File/Directory I/O, no IPlatform seam — casts are repo-local JSON, not the Windows/env-dependent
// paths IPlatform exists to make testable (Claustrum.Core.Platform.IPlatform doc comment).
public static class CastStore
{
    public const string DefaultName = "default";

    public static string DirectoryFor(string cwd) => Path.Combine(cwd, ".claustrum", "casts");

    public static string PathFor(string cwd, string name) => Path.Combine(DirectoryFor(cwd), $"{name}.json");

    public static bool Exists(string cwd, string name) => File.Exists(PathFor(cwd, name));

    // `run`/`delegate` fall back to `.claustrum/casts/default.json` when it exists and no --cast was
    // given (docs/PLAN.md §D1 "default: .claustrum/casts/default.json if present").
    public static Cast? TryLoadDefault(string cwd) => Exists(cwd, DefaultName) ? Load(cwd, DefaultName) : null;

    public static Cast Load(string cwd, string name)
    {
        string path = PathFor(cwd, name);
        if (!File.Exists(path))
            throw new CastException($"cast '{name}' not found at '{path}'");

        try
        {
            Cast cast = JsonSerializer.Deserialize(File.ReadAllText(path), CastJsonContext.Default.Cast)
                ?? throw new CastException($"'{path}' does not contain a JSON object");

            // Positional-record construction does not enforce non-nullable reference types at
            // runtime: a document that omits "roles" or "architect" deserialises those to null
            // instead of failing, and every caller (CastApplication.Resolve) assumes non-null
            // (review finding #3). Normalise the invariant here, once, for every caller.
            if (cast.Roles is null)
                throw new CastException($"'{path}': missing required field 'roles'");
            if (cast.Architect is null)
                throw new CastException($"'{path}': missing required field 'architect'");

            return cast;
        }
        catch (JsonException ex)
        {
            // ex.Message already carries the line/byte position; naming the file here is what a bare
            // JsonException wouldn't do (mirrors Config.ReadDocument's review finding #1).
            throw new CastException($"'{path}': {ex.Message}", ex);
        }
    }

    public static void Save(string cwd, Cast cast)
    {
        Directory.CreateDirectory(DirectoryFor(cwd));
        File.WriteAllText(PathFor(cwd, cast.Name), JsonSerializer.Serialize(cast, CastJsonContext.Default.Cast));
    }

    public static string[] ListNames(string cwd)
    {
        string directory = DirectoryFor(cwd);
        if (!Directory.Exists(directory))
            return [];

        return [.. Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)];
    }
}
