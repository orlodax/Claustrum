using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.claude/agents/&lt;role&gt;.md` (+ generated `-xhigh`/`-max` tier
/// stubs) and `.claude/skills/claustrum/SKILL.md` (docs/PLAN.md §B4/§D2 — renamed from `delegate`
/// once the skill grew a cast-questionnaire step). Idempotent by construction: a file without the
/// `claustrum:generated` marker is never overwritten unless `force` is set.
/// </summary>
public sealed class ClaudeSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "claude";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly SyncWriter writer = new(Harness, library.Version);

    /// <summary>
    /// <paramref name="desktop"/> is §B4's Claude desktop config target; it is merged only under
    /// <paramref name="global"/>, and a caller that has no desktop app to write to (Linux) passes null.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, ClaudeDesktopTarget? desktop = null)
    {
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 } ? roles : library.ListRoles();
        // `--global` targets `~/.claude/...` throughout, not just the agents dir (review finding #3:
        // the skill file used to hardcode `cwd` here regardless of `global`). `homeDirectory` is
        // caller-supplied (IPlatform.HomeDirectory in production) rather than read here directly, so
        // a test can redirect `--global` away from the real `~/.claude` (tester report, fault_in: code).
        string claudeRoot = global
            ? Path.Combine(homeDirectory, ".claude")
            : Path.Combine(cwd, ".claude");
        string agentsDir = Path.Combine(claudeRoot, "agents");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(agentsDir);

        SyncAccumulator into = new();

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteBaseAgent(agentsDir, role, loaded, cwd, force, mode, into);

            foreach (string tier in generatedTiers)
            {
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentsDir, role, tier, loaded, cwd, force, mode, into);
            }
        }

        WriteSkill(claudeRoot, force, mode, into);

        // .mcp.json/.vscode/mcp.json (docs/PLAN.md §B4/§D5) are repo-root files, not part of any
        // --global target (B4's --global list is agent directories and the desktop app's own config
        // file) — skipped entirely under --global, same as the manifest itself. The desktop app's
        // config is the mirror image: a --global-only target, and only where such an app exists.
        if (!global)
            MergeMcpConfigs(cwd, mode, force, into);
        else if (desktop is not null)
            MergeDesktopConfig(desktop, mode, force, into);

        if (!global && mode == SyncMode.Write)
            SyncManifestStore.Update(cwd, library.Version, into.ManifestFiles);

        return into.ToResult(mode);
    }

    private static void MergeMcpConfigs(string cwd, SyncMode mode, bool force, SyncAccumulator into)
    {
        // One manifest read for both targets: nothing between the two merges can change it.
        Dictionary<string, SyncManifestFile> existingManifest = SyncManifestStore.Read(cwd);

        JsonObject claudeCodeEntry = new() { ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        McpConfigSync.Merge(new McpConfigTarget(Harness, Path.Combine(cwd, ".mcp.json"), "mcpServers", claudeCodeEntry), mode, force, into, existingManifest);

        JsonObject vsCodeEntry = new() { ["type"] = "stdio", ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        McpConfigSync.Merge(new McpConfigTarget(Harness, Path.Combine(cwd, ".vscode", "mcp.json"), "servers", vsCodeEntry), mode, force, into, existingManifest);
    }

    // issue #24: the desktop app reads no repo, so `.claustrum/sync-manifest.json` cannot prove who
    // wrote the entry here — McpProvenance.OwnKey makes the `claustrum` key its own provenance, and
    // every other server in the file stays untouched exactly as in the repo targets.
    private static void MergeDesktopConfig(ClaudeDesktopTarget desktop, SyncMode mode, bool force, SyncAccumulator into)
    {
        JsonObject entry = new() { ["command"] = desktop.BinaryPath, ["args"] = new JsonArray("mcp") };
        McpConfigTarget target = new(Harness, desktop.ConfigPath, "mcpServers", entry, Provenance: McpProvenance.OwnKey);
        McpConfigSync.Merge(target, mode, force, into, ReadOnlyDictionary<string, SyncManifestFile>.Empty);
    }

    private void WriteBaseAgent(string agentsDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier tier = definition.Tiers["high"];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string frontmatter = BuildFrontmatter(role, definition.Description, ClaudeModelFor(tier.Model), tier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentsDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteTierStub(string agentsDir, string role, string tier, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier roleTier = definition.Tiers[tier];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string description = SyncWriter.TierDescription(role, tier);
        string frontmatter = BuildFrontmatter($"{role}-{tier}", description, ClaudeModelFor(roleTier.Model), roleTier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentsDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteSkill(string claudeRoot, bool force, SyncMode mode, SyncAccumulator into)
    {
        string skillDir = Path.Combine(claudeRoot, "skills", "claustrum");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(skillDir);
        string frontmatter = """
            ---
            name: claustrum
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend, and set up a cast (who plays which role) the first time.
            ---
            """;
        // docs/PLAN.md §D2: this skill replaces the old `delegate` one — Claustrum owns the cast
        // questionnaire, this skill is only the UI. The delegation half is unchanged from `delegate`.
        // Body is shared verbatim with every other harness's own /claustrum command/skill
        // (roles/_shared/claustrum-skill.md) — the interview and delegation steps are harness-neutral
        // by design; only the frontmatter format differs.
        string body = library.ReadShared("_shared/claustrum-skill.md");
        string path = Path.Combine(skillDir, "SKILL.md");
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into);
    }

    private static string BuildFrontmatter(string name, string description, string model, string effort, string color, string tools, string? disallowedTools)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {description}\n");
        builder.Append($"model: {model}\n");
        builder.Append($"effort: {effort}\n");
        builder.Append($"color: {color}\n");
        builder.Append($"tools: {tools}\n");
        if (disallowedTools is not null)
            builder.Append($"disallowedTools: {disallowedTools}\n");
        builder.Append("---");
        return builder.ToString();
    }

    // Claude subagent frontmatter needs a literal model name, independent of Core's model-class
    // resolution (A7) — see NOTES.md "ClaudeSync model mapping".
    private static string ClaudeModelFor(string modelClass) => modelClass switch
    {
        "frontier-reasoning" or "frontier-coding" => "opus",
        "standard-coding" => "sonnet",
        "cheap-coding" or "fast" => "haiku",
        _ => throw new RoleRenderException($"no Claude model mapping for class '{modelClass}'"),
    };

    private static (string Tools, string? DisallowedTools) ToolsFor(RoleDefinition definition)
    {
        if (definition.Permission == "readonly")
            return ("Read, Grep, Glob, Bash, PowerShell, WebFetch, WebSearch", "Agent, Edit, Write");

        string editTools = "Read, Grep, Glob, Bash, PowerShell, Edit, Write, NotebookEdit, WebFetch, WebSearch";
        return definition.MayDelegate.Length > 0 ? ($"{editTools}, Agent", null) : (editTools, null);
    }
}
