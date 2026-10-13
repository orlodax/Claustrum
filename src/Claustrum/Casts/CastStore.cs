using System.Text.Json;
using Claustrum.Roles.Sync;

namespace Claustrum.Casts;

// `.claustrum/casts/<name>.json` (docs/PLAN.md §D1). Mirrors JobDirectory/ClaudeSync: plain
// File/Directory I/O, no IPlatform seam — casts are repo-local JSON, not the Windows/env-dependent
// paths IPlatform exists to make testable (Claustrum.Core.Platform.IPlatform doc comment).
// Every read, write and directory creation passes SyncRoot.Repo(cwd) on the full path, leaf included, and
// the fence comes BEFORE any File/Directory.Exists: a committed link must not carry a cast out of the cwd, nor make
// a stat contact a UNC host (#103, #105, NOTES "Casts are fenced like sync").
public static class CastStore
{
    public const string DefaultName = "default";

    public static string DirectoryFor(string cwd) => Path.Combine(cwd, ".claustrum", "casts");

    public static string PathFor(string cwd, string name) => Path.Combine(DirectoryFor(cwd), $"{name}.json");

    public static bool Exists(string cwd, string name)
    {
        string path = PathFor(cwd, name);
        return MayStat(cwd, path, "read") && File.Exists(path);
    }

    // `run`/`delegate` fall back to `.claustrum/casts/default.json` when it exists and no --cast was
    // given (docs/PLAN.md §D1 "default: .claustrum/casts/default.json if present").
    public static Cast? TryLoadDefault(string cwd) => Exists(cwd, DefaultName) ? Load(cwd, DefaultName) : null;

    public static Cast Load(string cwd, string name)
    {
        string path = PathFor(cwd, name);
        if (!Exists(cwd, name))
            throw new CastException($"cast '{name}' not found at '{path}'");

        try
        {
            // A FIFO passes File.Exists and would block the read forever (2026-10-13, found by review).
            using FileStream stream = PlainFile.OpenRead(path, out string? refusal)
                ?? throw new CastException($"'{path}': {refusal}");

            using StreamReader reader = new(stream);
            Cast cast = JsonSerializer.Deserialize(reader.ReadToEnd(), CastJsonContext.Default.Cast)
                ?? throw new CastException($"'{path}' does not contain a JSON object");

            // Positional-record construction does not enforce non-nullable reference types at
            // runtime: a document that omits "roles" or "architect" deserialises those to null
            // instead of failing, and every caller (CastApplication.Resolve) assumes non-null
            // (review finding #3). Normalise the invariant here, once, for every caller.
            if (cast.Roles is null)
                throw new CastException($"'{path}': missing required field 'roles'");
            if (cast.Architect is null)
                throw new CastException($"'{path}': missing required field 'architect'");

            // A hand-written cast may leave these out: the file's own name and the shipped library stand in, so
            // `cast show` and the budget ledger's cast key never see a null (#67).
            if (string.IsNullOrWhiteSpace(cast.Name))
                cast = cast with { Name = name };
            if (string.IsNullOrWhiteSpace(cast.Library))
                cast = cast with { Library = AppServices.RoleLibrary.Version };

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
        string path = PathFor(cwd, cast.Name);
        RequireInside(cwd, path, "written");
        Directory.CreateDirectory(DirectoryFor(cwd));
        File.WriteAllText(path, JsonSerializer.Serialize(cast, CastJsonContext.Default.Cast));
    }

    public static string[] ListNames(string cwd)
    {
        string directory = DirectoryFor(cwd);
        if (!MayStat(cwd, directory, "listed") || !Directory.Exists(directory))
            return [];

        return [.. Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    // The fence root is the cwd the caller uses, never the git root: a `.git`-less project under `git init ~`
    // would widen it to `~`. Over-refusal (a loop, a link the walk cannot place) beats a stray write.
    private static void RequireInside(string cwd, string path, string verb)
    {
        if (SyncRoot.Repo(cwd).Escapes(path))
            throw Refusal(path, verb);
    }

    // The fence before any stat (#105): true when the path stays inside and may be statted; false when it leaves the
    // cwd with nothing at its real path (no such cast); an escaping path with something there throws.
    private static bool MayStat(string cwd, string path, string verb) => SyncRoot.Repo(cwd).ProbeEscaping(path) switch
    {
        EscapeVerdict.Inside => true,
        EscapeVerdict.Absent => false,
        _ => throw Refusal(path, verb),
    };

    private static CastException Refusal(string path, string verb) =>
        new($"'{path}' resolves outside the working directory ({SyncRoot.Resolve(path)}); a cast is never {verb} through a link that leaves it");
}
