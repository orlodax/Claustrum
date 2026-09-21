using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Roles.Json;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Mirrors McpConfigSyncTests, but for OpencodeSync's own registration of the claustrum MCP server in
// opencode.json's top-level "mcp" key (issue #15) — same McpConfigSync.Merge underneath, so most of
// the idempotency/foreign-key behaviour is identical; what differs is the array-shaped `command`, the
// create-only `$schema`, and the `.jsonc` name opencode also accepts (NOTES.md "The claustrum MCP
// server is registered in opencode.json too").
public sealed class OpencodeMcpConfigSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-opencode-mcp-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-opencode-mcp-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        // The checkout-move regression test below relocates `cwd`, so it may no longer exist by the
        // time this runs (McpConfigSyncTests' own Dispose carries the same guard).
        if (Directory.Exists(cwd))
            Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private OpencodeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshWriteRegistersTheClaustrumServerWithSchemaFirst()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        Assert.Contains(opencodeJsonPath, result.Written);

        JsonObject root = JsonNode.Parse(File.ReadAllText(opencodeJsonPath))!.AsObject();
        Assert.Equal("local", root["mcp"]!["claustrum"]!["type"]!.GetValue<string>());
        Assert.Equal(["claustrum", "mcp"], root["mcp"]!["claustrum"]!["command"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("https://opencode.ai/config.json", root["$schema"]!.GetValue<string>());
        Assert.Equal("$schema", root.First().Key);
    }

    [Fact]
    public void RerunIsIdempotentForTheMcpFileToo()
    {
        OpencodeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        Assert.DoesNotContain(opencodeJsonPath, second.Written);
        Assert.Contains(opencodeJsonPath, second.Skipped);
    }

    [Fact]
    public void ForeignMcpServerAndTopLevelKeySurviveTheMergeWithNoSchemaAdded()
    {
        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        File.WriteAllText(opencodeJsonPath, /*lang=json,strict*/
            """{"mcp":{"other-tool":{"type":"local","command":["other"]}},"theme":"dark"}""");

        NewSync().Sync(cwd, roles: ["builder"]);

        JsonObject result = JsonNode.Parse(File.ReadAllText(opencodeJsonPath))!.AsObject();
        Assert.Equal("other", result["mcp"]!["other-tool"]!["command"]![0]!.GetValue<string>());
        Assert.Equal("dark", result["theme"]!.GetValue<string>());
        Assert.Equal("local", result["mcp"]!["claustrum"]!["type"]!.GetValue<string>());
        Assert.False(result.ContainsKey("$schema"));
    }

    // The other half of the same rule: a config a human already gave its own $schema (pointing
    // wherever they like) is never rewritten either — RootOnCreate only seeds a file created from
    // nothing (McpConfigSync.Merge only reads target.RootOnCreate on the !File.Exists path).
    [Fact]
    public void AnExistingSchemaOnAHumanConfiguredFileIsNeverRewritten()
    {
        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        File.WriteAllText(opencodeJsonPath, /*lang=json,strict*/ """{"$schema":"https://example.com/other-schema.json"}""");

        NewSync().Sync(cwd, roles: ["builder"]);

        JsonObject result = JsonNode.Parse(File.ReadAllText(opencodeJsonPath))!.AsObject();
        Assert.Equal("https://example.com/other-schema.json", result["$schema"]!.GetValue<string>());
        Assert.Equal("local", result["mcp"]!["claustrum"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void HandWrittenClaustrumEntryWithNoManifestRecordIsForeignWithoutForce()
    {
        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        const string original = /*lang=json,strict*/ """{"mcp":{"claustrum":{"type":"local","command":["/hand/edited/claustrum"]}}}""";
        File.WriteAllText(opencodeJsonPath, original);

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(opencodeJsonPath, result.Foreign);
        Assert.Equal(original, File.ReadAllText(opencodeJsonPath));
    }

    [Fact]
    public void ForceAdoptsAHandWrittenClaustrumEntry()
    {
        string opencodeJsonPath = Path.Combine(cwd, "opencode.json");
        File.WriteAllText(opencodeJsonPath, /*lang=json,strict*/
            """{"mcp":{"claustrum":{"type":"local","command":["/hand/edited/claustrum"]}}}""");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(opencodeJsonPath, result.Written);
        JsonNode json = JsonNode.Parse(File.ReadAllText(opencodeJsonPath))!;
        Assert.Equal("claustrum", json["mcp"]!["claustrum"]!["command"]![0]!.GetValue<string>());
    }

    [Fact]
    public void DryRunNeverWritesTheOpencodeJsonFile()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("opencode.json", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(cwd, "opencode.json")));
        Assert.Contains(result.ProposedContent!.Keys, p => p.EndsWith("opencode.json", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalSyncNeverTouchesOpencodeJsonOrTheManifest()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        Assert.DoesNotContain(result.Written, p => p.EndsWith("opencode.json", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(cwd, "opencode.json")));
        Assert.False(File.Exists(Path.Combine(cwd, ".claustrum", "sync-manifest.json")));
    }

    // opencode reads either extension; McpConfigSync's own ParseExisting tolerates the comments and
    // trailing commas VS Code/opencode's own JSONC dialect allows.
    [Fact]
    public void OnlyOpencodeJsoncExistsIsWrittenAndKeepsItsForeignServerAndComment()
    {
        string jsoncPath = Path.Combine(cwd, "opencode.jsonc");
        File.WriteAllText(jsoncPath, /*lang=json*/ """
            {
              // hand-edited: see team wiki
              "mcp": { "fetch": { "type": "local", "command": ["uvx", "mcp-server-fetch"] }, },
            }
            """);

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(jsoncPath, result.Written);
        JsonNode json = JsonNode.Parse(File.ReadAllText(jsoncPath))!;
        Assert.Equal("uvx", json["mcp"]!["fetch"]!["command"]![0]!.GetValue<string>());
        Assert.Equal("local", json["mcp"]!["claustrum"]!["type"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(cwd, "opencode.json")));
    }

    [Fact]
    public void BothOpencodeJsonAndJsoncExistOnlyTheJsonFileIsTouched()
    {
        string jsonPath = Path.Combine(cwd, "opencode.json");
        string jsoncPath = Path.Combine(cwd, "opencode.jsonc");
        File.WriteAllText(jsonPath, /*lang=json,strict*/ """{"theme":"dark"}""");
        const string jsoncOriginal = /*lang=json,strict*/ """{"theme":"light"}""";
        File.WriteAllText(jsoncPath, jsoncOriginal);

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(jsonPath, result.Written);
        Assert.DoesNotContain(jsoncPath, result.Written);
        Assert.DoesNotContain(jsoncPath, result.Skipped);
        Assert.DoesNotContain(jsoncPath, result.Foreign);
        Assert.Equal(jsoncOriginal, File.ReadAllText(jsoncPath));
        Assert.Equal("local", JsonNode.Parse(File.ReadAllText(jsonPath))!["mcp"]!["claustrum"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void MalformedOpencodeJsonNamesTheFileInsteadOfCrashing()
    {
        File.WriteAllText(Path.Combine(cwd, "opencode.json"), "{ not json");

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => NewSync().Sync(cwd, roles: ["builder"]));

        Assert.Contains("opencode.json", exception.Message, StringComparison.Ordinal);
    }

    // Mirrors McpConfigSyncTests.ManifestRecordedUnderADifferentCheckoutPathIsStillRecognizedAsOurs:
    // the manifest's provenance check is by content hash, not by where the checkout happens to sit,
    // so a repo built from a different path (or OS — AGENTS.md builds both natively) still recognizes
    // its own past entry once relocated.
    [Fact]
    public void ManifestRecordedUnderADifferentCheckoutPathIsStillRecognizedAsOurs()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string movedCwd = Path.Combine(Path.GetDirectoryName(cwd)!, $"{Path.GetFileName(cwd)}-moved");
        Directory.Move(cwd, movedCwd);

        const string oldEntryJson = /*lang=json,strict*/ """{"type":"local","command":["claustrum-old","mcp"]}""";
        string opencodeJsonPath = Path.Combine(movedCwd, "opencode.json");
        File.WriteAllText(opencodeJsonPath, """{"mcp":{"claustrum":""" + oldEntryJson + "}}");

        SyncManifest manifest = new("test", [new SyncManifestFile("opencode.json", "_mcp", "opencode", Sha256Hex(oldEntryJson))]);
        File.WriteAllText(Path.Combine(movedCwd, ".claustrum", "sync-manifest.json"), JsonSerializer.Serialize(manifest, RolesJsonContext.Default.SyncManifest));

        SyncResult result = new OpencodeSync(library, new RoleRenderer(library), fakeHome).Sync(movedCwd, roles: ["builder"], force: false);

        Assert.Contains(opencodeJsonPath, result.Written);
        Assert.DoesNotContain(opencodeJsonPath, result.Foreign);
        Assert.Equal("local", JsonNode.Parse(File.ReadAllText(opencodeJsonPath))!["mcp"]!["claustrum"]!["type"]!.GetValue<string>());

        Directory.Delete(movedCwd, recursive: true);
    }

    // docs/PLAN.md's `sync --only claude,opencode` semantics: two harnesses writing into one
    // .claustrum/sync-manifest.json via SyncManifestStore's merge-by-path (NOTES.md "The claustrum MCP
    // server is registered in opencode.json too" — "one run ... produces a manifest holding all eleven
    // paths ... and the rerun reports every one of them skipped").
    [Fact]
    public void SyncOnlyClaudeThenOpencodeLeaveOneManifestHoldingBothHarnessesPaths()
    {
        ClaudeSync claudeSync = new(library, new RoleRenderer(library), fakeHome);
        OpencodeSync opencodeSync = NewSync();

        claudeSync.Sync(cwd, roles: ["builder"]);
        opencodeSync.Sync(cwd, roles: ["builder"]);

        SyncManifest manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(cwd, ".claustrum", "sync-manifest.json")), RolesJsonContext.Default.SyncManifest)!;

        string[] paths = [.. manifest.Files.Select(file => file.Path)];
        Assert.Equal(11, paths.Length);
        Assert.Contains(".mcp.json", paths);
        Assert.Contains(".vscode/mcp.json", paths);
        Assert.Contains("opencode.json", paths);
        Assert.Contains(".claude/agents/builder.md", paths);
        Assert.Contains(".opencode/agent/builder.md", paths);
        Assert.Contains(".opencode/command/claustrum.md", paths);

        SyncResult claudeRerun = claudeSync.Sync(cwd, roles: ["builder"]);
        SyncResult opencodeRerun = opencodeSync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(claudeRerun.Written);
        Assert.Empty(claudeRerun.Foreign);
        Assert.NotEmpty(claudeRerun.Skipped);
        Assert.Empty(opencodeRerun.Written);
        Assert.Empty(opencodeRerun.Foreign);
        Assert.NotEmpty(opencodeRerun.Skipped);
    }

    private static string Sha256Hex(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
