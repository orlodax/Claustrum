namespace Claustrum.Roles.Model;

/// <summary>One file `claustrum sync` produced, tracked so a future non-marker target (e.g. JSON) is still idempotent.</summary>
public sealed record SyncManifestFile(string Path, string Role, string Harness, string Sha256);

/// <summary>`.claustrum/sync-manifest.json` (docs/PLAN.md §B4) — every file the last `sync` run wrote, across roles/harnesses.</summary>
public sealed record SyncManifest(string LibraryVersion, List<SyncManifestFile> Files);
