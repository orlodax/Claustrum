using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Merges the <c>claustrum</c> MCP server registration into <c>.mcp.json</c> (Claude Code and other
/// hosts that read it) and <c>.vscode/mcp.json</c> (VS Code), touching only that one key so any other
/// server a human or another tool registered survives untouched (docs/PLAN.md §B4/§D5). JSON has no
/// room for the inline <c>claustrum:generated</c> marker <see cref="ClaudeSync"/>'s Markdown targets
/// carry, so idempotency and foreign-file protection are decided from <c>sync-manifest.json</c>
/// instead: a <c>claustrum</c> key with no matching manifest entry is a human's, left untouched
/// without <c>--force</c>.
/// </summary>
internal static class McpConfigSync
{
    public static void Sync(
        string cwd, SyncMode mode, bool force,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles,
        Dictionary<string, string> proposedContent, IReadOnlyDictionary<string, SyncManifestFile> existingManifest)
    {
        JsonObject claudeCodeEntry = new() { ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        SyncOneFile(Path.Combine(cwd, ".mcp.json"), "mcpServers", claudeCodeEntry, mode, force, written, skipped, foreign, manifestFiles, proposedContent, existingManifest);

        JsonObject vsCodeEntry = new() { ["type"] = "stdio", ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        SyncOneFile(Path.Combine(cwd, ".vscode", "mcp.json"), "servers", vsCodeEntry, mode, force, written, skipped, foreign, manifestFiles, proposedContent, existingManifest);
    }

    private static void SyncOneFile(
        string path, string sectionKey, JsonObject claustrumEntry, SyncMode mode, bool force,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles,
        Dictionary<string, string> proposedContent, IReadOnlyDictionary<string, SyncManifestFile> existingManifest)
    {
        JsonObject root = File.Exists(path) ? ParseExisting(path) : [];

        JsonObject section = root[sectionKey] as JsonObject ?? [];
        JsonNode? existingEntry = section["claustrum"];
        string newSha256 = ComputeSha256(claustrumEntry.ToJsonString());

        // DeepEquals compares structure, not formatting/property order — a human's hand-formatted
        // file that already has the exact right entry must still be treated as up to date.
        if (JsonNode.DeepEquals(existingEntry, claustrumEntry))
        {
            skipped.Add(path);
            manifestFiles.Add(new SyncManifestFile(path, "_mcp", "claude", newSha256));
            return;
        }

        bool weWroteTheExistingEntry = existingEntry is not null
            && existingManifest.TryGetValue(path, out SyncManifestFile? recorded)
            && recorded.Sha256 == ComputeSha256(existingEntry.ToJsonString());

        if (existingEntry is not null && !weWroteTheExistingEntry && !force)
        {
            foreign.Add(path);
            return;
        }

        section["claustrum"] = claustrumEntry.DeepClone();
        root[sectionKey] = section;

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
            proposedContent[path] = content;
        }

        written.Add(path);
        manifestFiles.Add(new SyncManifestFile(path, "_mcp", "claude", newSha256));
    }

    // VS Code documents `.vscode/*.json` as JSONC (comments + trailing commas allowed), and a
    // `.vscode/mcp.json` a human hand-wrote commonly has both. Parsing with the default, strict
    // JsonDocumentOptions threw an uncaught JsonException here (review finding #1) that named
    // neither the file nor the cause, and — because McpConfigSync runs after every Markdown target
    // — left the sync half-applied with the manifest never updated.
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
