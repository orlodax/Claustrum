using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Merges the <c>claustrum</c> MCP server registration into one harness-declared JSON config file,
/// touching only that one key so any other server a human or another tool registered survives
/// untouched (docs/PLAN.md §B4/§D5). Each harness's Sync owns its <see cref="McpConfigTarget"/>s —
/// ClaudeSync's <c>.mcp.json</c>/<c>.vscode/mcp.json</c>, OpencodeSync's <c>opencode.json</c> — since
/// where the file lives and what the entry looks like is exactly what differs between them. JSON has
/// no room for the inline <c>claustrum:generated</c> marker <see cref="SyncWriter"/>'s Markdown
/// targets carry, so idempotency and foreign-key protection are decided from
/// <see cref="SyncManifestStore"/> instead: a <c>claustrum</c> key with no matching manifest entry is
/// a human's, left untouched without <c>--force</c>.
/// </summary>
internal static class McpConfigSync
{
    public static void Merge(
        McpConfigTarget target, SyncMode mode, bool force,
        SyncAccumulator into, IReadOnlyDictionary<string, SyncManifestFile> existingManifest)
    {
        string path = target.Path;
        JsonObject root = File.Exists(path) ? ParseExisting(path) : target.RootOnCreate?.DeepClone().AsObject() ?? [];

        JsonObject section = root[target.SectionKey] as JsonObject ?? [];
        JsonNode? existingEntry = section["claustrum"];
        string newSha256 = ComputeSha256(target.Entry.ToJsonString());

        // DeepEquals compares structure, not formatting/property order — a human's hand-formatted
        // file that already has the exact right entry must still be treated as up to date.
        if (JsonNode.DeepEquals(existingEntry, target.Entry))
        {
            into.Skipped.Add(path);
            into.ManifestFiles.Add(new SyncManifestFile(path, "_mcp", target.Harness, newSha256));
            return;
        }

        bool weWroteTheExistingEntry = existingEntry is not null
            && existingManifest.TryGetValue(path, out SyncManifestFile? recorded)
            && recorded.Sha256 == ComputeSha256(existingEntry.ToJsonString());

        if (existingEntry is not null && !weWroteTheExistingEntry && !force)
        {
            into.Foreign.Add(path);
            return;
        }

        section["claustrum"] = target.Entry.DeepClone();
        root[target.SectionKey] = section;

        // Round-tripping through JsonNode drops any `//` comments a human left elsewhere in the
        // file (JsonObject has no comment slots to preserve them in). The common re-sync path is
        // the DeepEquals early-return above, which never reaches here, so comments survive an
        // idempotent sync; they are lost only when the claustrum entry genuinely has to change.
        string content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (mode == SyncMode.Write)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 })
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, content);
        }
        else
        {
            into.ProposedContent[path] = content;
        }

        into.Written.Add(path);
        into.ManifestFiles.Add(new SyncManifestFile(path, "_mcp", target.Harness, newSha256));
    }

    // VS Code documents `.vscode/*.json` as JSONC (comments + trailing commas allowed) and opencode
    // reads `opencode.jsonc` by name, so a hand-written target commonly has both. Parsing with the
    // default, strict JsonDocumentOptions threw an uncaught JsonException here (review finding #1)
    // that named neither the file nor the cause, and — because the JSON merge runs after every
    // Markdown target — left the sync half-applied with the manifest never updated.
    private static JsonObject ParseExisting(string path)
    {
        JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: options) as JsonObject
                ?? throw new RoleRenderException($"'{path}' does not contain a JSON object");
        }
        catch (JsonException exception)
        {
            throw new RoleRenderException($"'{path}' is not valid JSON: {exception.Message}");
        }
    }

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}

/// <summary>
/// One JSON config file a harness wants the <c>claustrum</c> server registered in:
/// <paramref name="Entry"/> goes at <c>root[SectionKey]["claustrum"]</c>, and
/// <paramref name="Harness"/> is what the <c>sync-manifest.json</c> record is attributed to.
/// <paramref name="RootOnCreate"/> seeds the root object when the file does not exist yet (opencode's
/// <c>$schema</c>); merging into an existing file never adds those properties.
/// </summary>
internal sealed record McpConfigTarget(
    string Harness, string Path, string SectionKey, JsonObject Entry, JsonObject? RootOnCreate = null);
