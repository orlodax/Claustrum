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
        JsonObject root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new RoleRenderException($"'{path}' does not contain a JSON object")
            : [];

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

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
