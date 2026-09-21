using System.Text.Json;
using Claustrum.Roles.Json;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// `.claustrum/sync-manifest.json` I/O, shared by every harness Sync that merges a JSON target
/// (<see cref="McpConfigSync"/>). It is the only way to tell "claustrum wrote this JSON key last
/// time, safe to overwrite" from "a human put unrelated content there": JSON has no room for the
/// inline claustrum:generated marker <see cref="SyncWriter"/>'s Markdown targets carry
/// (<see cref="SyncManifestFile"/>'s own doc comment: "so a future non-marker target (e.g. JSON) is
/// still idempotent"). Paths are absolute in memory and stored relative to cwd.
/// </summary>
internal static class SyncManifestStore
{
    /// <summary>Paths come back absolute, so every caller keeps treating SyncManifestFile.Path as absolute in memory.</summary>
    public static Dictionary<string, SyncManifestFile> Read(string cwd)
    {
        string manifestPath = ManifestPath(cwd);
        if (!File.Exists(manifestPath))
            return [];

        SyncManifest? existing = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), RolesJsonContext.Default.SyncManifest);
        Dictionary<string, SyncManifestFile> byPath = [];
        foreach (SyncManifestFile file in existing?.Files ?? [])
        {
            // Path.GetFullPath(path, basePath) resolves a portable, cwd-relative stored path back
            // to absolute, and passes an already-absolute legacy entry through unchanged, so an
            // old manifest full of absolute paths keeps working with no migration.
            string absolutePath = Path.GetFullPath(file.Path, cwd);
            byPath[absolutePath] = file with { Path = absolutePath };
        }

        return byPath;
    }

    /// <summary>Merges <paramref name="manifestFiles"/> into what is already on disk, keyed by path.</summary>
    public static void Update(string cwd, string libraryVersion, List<SyncManifestFile> manifestFiles)
    {
        // Merging by path (rather than replacing the file) is what lets two harnesses in one
        // `sync --only claude,opencode` run both call this without erasing each other's entries.
        Dictionary<string, SyncManifestFile> merged = Read(cwd);
        foreach (SyncManifestFile file in manifestFiles)
            merged[file.Path] = file;

        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum"));

        // Stored relative to cwd with '/' separators (review finding #5): an absolute path only
        // matches a sync run from the exact same checkout location, so building this repo from
        // both Windows and Linux (AGENTS.md) — or any worktree, CI checkout, or rename — made
        // claustrum's own entries look foreign forever. Read resolves the path back.
        List<SyncManifestFile> portable = [.. merged.Values
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f with { Path = Path.GetRelativePath(cwd, f.Path).Replace('\\', '/') })];
        SyncManifest manifest = new(libraryVersion, portable);
        File.WriteAllText(ManifestPath(cwd), JsonSerializer.Serialize(manifest, RolesJsonContext.Default.SyncManifest));
    }

    private static string ManifestPath(string cwd) => Path.Combine(cwd, ".claustrum", "sync-manifest.json");
}
