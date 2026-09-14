using System.Text.Json.Nodes;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// McpConfigSync is internal (invoked from ClaudeSync.Sync — docs/PLAN.md §B4/§D5), so these drive it
// through the public Sync() entry point and inspect the .mcp.json/.vscode/mcp.json it writes.
public sealed class McpConfigSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-mcp-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private ClaudeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesBothMcpFilesWithTheClaustrumEntry()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        string mcpJsonPath = Path.Combine(cwd, ".mcp.json");
        string vscodePath = Path.Combine(cwd, ".vscode", "mcp.json");
        Assert.Contains(mcpJsonPath, result.Written);
        Assert.Contains(vscodePath, result.Written);

        JsonNode mcpJson = JsonNode.Parse(File.ReadAllText(mcpJsonPath))!;
        Assert.Equal("claustrum", mcpJson["mcpServers"]!["claustrum"]!["command"]!.GetValue<string>());

        JsonNode vscodeJson = JsonNode.Parse(File.ReadAllText(vscodePath))!;
        Assert.Equal("stdio", vscodeJson["servers"]!["claustrum"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void RerunIsIdempotentForMcpFilesToo()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        string mcpJsonPath = Path.Combine(cwd, ".mcp.json");
        Assert.DoesNotContain(mcpJsonPath, second.Written);
        Assert.Contains(mcpJsonPath, second.Skipped);
    }

    [Fact]
    public void ForeignSiblingServersAndKeysSurviveTheMerge()
    {
        string mcpJsonPath = Path.Combine(cwd, ".mcp.json");
        File.WriteAllText(mcpJsonPath, /*lang=json,strict*/ """{"mcpServers":{"other-tool":{"command":"other"}},"unrelatedTopLevelKey":true}""");

        NewSync().Sync(cwd, roles: ["builder"]);

        JsonNode result = JsonNode.Parse(File.ReadAllText(mcpJsonPath))!;
        Assert.Equal("other", result["mcpServers"]!["other-tool"]!["command"]!.GetValue<string>());
        Assert.True(result["unrelatedTopLevelKey"]!.GetValue<bool>());
        Assert.Equal("claustrum", result["mcpServers"]!["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void HandEditedClaustrumEntryWithNoManifestRecordIsForeignWithoutForce()
    {
        string mcpJsonPath = Path.Combine(cwd, ".mcp.json");
        File.WriteAllText(mcpJsonPath, /*lang=json,strict*/ """{"mcpServers":{"claustrum":{"command":"/some/hand/edited/path","args":["mcp"]}}}""");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(mcpJsonPath, result.Foreign);
        Assert.Equal("/some/hand/edited/path", JsonNode.Parse(File.ReadAllText(mcpJsonPath))!["mcpServers"]!["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void ForceAdoptsAHandEditedClaustrumEntry()
    {
        string mcpJsonPath = Path.Combine(cwd, ".mcp.json");
        File.WriteAllText(mcpJsonPath, /*lang=json,strict*/ """{"mcpServers":{"claustrum":{"command":"/some/hand/edited/path","args":["mcp"]}}}""");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(mcpJsonPath, result.Written);
        Assert.Equal("claustrum", JsonNode.Parse(File.ReadAllText(mcpJsonPath))!["mcpServers"]!["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void DryRunNeverWritesTheMcpFiles()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith(".mcp.json", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(cwd, ".mcp.json")));
        Assert.Contains(result.ProposedContent!.Keys, p => p.EndsWith(".mcp.json", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalSyncNeverTouchesMcpFiles()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        Assert.DoesNotContain(result.Written, p => p.EndsWith(".mcp.json", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(cwd, ".mcp.json")));
    }

    // Finding #1: VS Code documents `.vscode/*.json` as JSONC, so a hand-written `.vscode/mcp.json`
    // commonly has a `//` comment. Parsing with default (strict) JsonDocumentOptions threw an
    // uncaught JsonException here and left the whole sync half-applied.
    [Fact]
    public void JsoncVsCodeMcpFileWithACommentSyncsCleanlyAndKeepsTheForeignServer()
    {
        string vscodeDir = Path.Combine(cwd, ".vscode");
        Directory.CreateDirectory(vscodeDir);
        string vscodePath = Path.Combine(vscodeDir, "mcp.json");
        File.WriteAllText(vscodePath, /*lang=json*/ """
            {
              // Managed by hand: see the team wiki
              "servers": { "fetch": { "type": "stdio", "command": "uvx", "args": ["mcp-server-fetch"] } }
            }
            """);

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(vscodePath, result.Written);
        JsonNode json = JsonNode.Parse(File.ReadAllText(vscodePath))!;
        Assert.Equal("uvx", json["servers"]!["fetch"]!["command"]!.GetValue<string>());
        Assert.Equal("claustrum", json["servers"]!["claustrum"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedJsonNamesTheFileInsteadOfCrashingWithARawJsonException()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), "{ not json");

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => NewSync().Sync(cwd, roles: ["builder"]));

        Assert.Contains(".mcp.json", exception.Message, StringComparison.Ordinal);
    }
}
