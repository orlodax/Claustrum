using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using Claustrum.Core.Config;
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

    /// <summary>The one place `~/.claude` is spelled, shared by the fallback below and the CLI's resolver.</summary>
    public static string DefaultGlobalRoot(string homeDirectory) => Path.Combine(homeDirectory, ".claude");

    /// <summary>
    /// <paramref name="desktop"/> is §B4's Claude desktop config target; it is merged only under
    /// <paramref name="global"/>, and a caller that has no desktop app to write to (Linux) passes null.
    /// <paramref name="globalRoot"/> is the global root the caller already resolved from the
    /// environment (`CLAUDE_CONFIG_DIR`); null means the default under `homeDirectory`.
    /// <paramref name="config"/> is the merged `claustrum.json` the agents' model and effort follow (#53); null means
    /// the hard-coded defaults.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, ClaudeDesktopTarget? desktop = null, string? globalRoot = null, Config? config = null)
    {
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 } ? roles : library.ListRoles();
        // `--global` renders the library alone: a repo's `.claustrum/roles/` applies to that repo, and its
        // files must not leak into the user-global agents (issue #99).
        string? overrideCwd = global ? null : cwd;

        // `--global` targets the global root throughout, not just the agents dir (review finding #3:
        // the skill file used to hardcode `cwd` here regardless of `global`). The root and
        // `homeDirectory` are caller-supplied, never read from the environment here, so a test can
        // redirect `--global` away from the real `~/.claude` (tester report, fault_in: code).
        string claudeRoot = global
            ? globalRoot ?? DefaultGlobalRoot(homeDirectory)
            : Path.Combine(cwd, ".claude");
        // 2026-10-12: the cwd, not the git root: `GitRootLocator` widens the fence to `~` for a `.git`-less
        // project under a `git init ~` home, and the issue's overwrite returns (found in re-review).
        SyncRoot root = global ? SyncRoot.Global(claudeRoot) : SyncRoot.Repo(cwd);
        string agentsDir = Path.Combine(claudeRoot, "agents");
        if (mode == SyncMode.Write && !root.Escapes(agentsDir))
            Directory.CreateDirectory(agentsDir);

        SyncAccumulator into = new();
        SyncSettings settings = new(config);
        if (!global)
            SyncManifestStore.RefuseIfEscaping(cwd, root, into);

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, overrideCwd);
            if (global && RoleLibrary.HasLocalOverride(role, cwd))
                into.IgnoredOverrides.Add(role);

            WriteBaseAgent(agentsDir, role, loaded, settings, overrideCwd, force, mode, into, root);

            foreach (string tier in generatedTiers)
            {
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentsDir, role, tier, loaded, settings, overrideCwd, force, mode, into, root);
            }
        }

        WriteSkill(claudeRoot, force, mode, into, root);

        // .mcp.json/.vscode/mcp.json (docs/PLAN.md §B4/§D5) are repo-root files, not part of any
        // --global target (B4's --global list is agent directories and the desktop app's own config
        // file) — skipped entirely under --global, same as the manifest itself. The desktop app's
        // config is the mirror image: a --global-only target, and only where such an app exists.
        if (!global)
            MergeMcpConfigs(cwd, mode, force, into, root);
        else if (desktop is not null)
            MergeDesktopConfig(desktop, mode, force, into, root);

        if (!global && mode == SyncMode.Write)
            SyncManifestStore.Update(cwd, library.Version, into);

        return into.ToResult(mode);
    }

    private static void MergeMcpConfigs(string cwd, SyncMode mode, bool force, SyncAccumulator into, SyncRoot root)
    {
        // One manifest read for both targets: nothing between the two merges can change it.
        Dictionary<string, SyncManifestFile> existingManifest = SyncManifestStore.Read(cwd, into);

        JsonObject claudeCodeEntry = new() { ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        McpConfigSync.Merge(new McpConfigTarget(Harness, Path.Combine(cwd, ".mcp.json"), "mcpServers", claudeCodeEntry), mode, force, into, existingManifest, root);

        JsonObject vsCodeEntry = new() { ["type"] = "stdio", ["command"] = "claustrum", ["args"] = new JsonArray("mcp") };
        McpConfigSync.Merge(new McpConfigTarget(Harness, Path.Combine(cwd, ".vscode", "mcp.json"), "servers", vsCodeEntry), mode, force, into, existingManifest, root);
    }

    // issue #24: the desktop app reads no repo, so `.claustrum/sync-manifest.json` cannot prove who
    // wrote the entry here — McpProvenance.OwnKey makes the `claustrum` key its own provenance, and
    // every other server in the file stays untouched exactly as in the repo targets.
    private static void MergeDesktopConfig(ClaudeDesktopTarget desktop, SyncMode mode, bool force, SyncAccumulator into, SyncRoot root)
    {
        JsonObject entry = new() { ["command"] = desktop.BinaryPath, ["args"] = new JsonArray("mcp") };
        McpConfigTarget target = new(Harness, desktop.ConfigPath, "mcpServers", entry, Provenance: McpProvenance.OwnKey);
        McpConfigSync.Merge(target, mode, force, into, ReadOnlyDictionary<string, SyncManifestFile>.Empty, root);
    }

    private void WriteBaseAgent(string agentsDir, string role, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, "high", definition.Tiers["high"], ClaudeModelFor);
        SyncSettings.RecordModel(into, Harness, role, choice.Model, choice.Reason);
        SyncSettings.RecordUnrenderedDeny(into, Harness, role, choice);
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string frontmatter = BuildFrontmatter(role, definition.Description, choice.Model, choice.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.Render(role, "high", Harness, overrideCwd).SystemBody;
        string path = Path.Combine(agentsDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteTierStub(string agentsDir, string role, string tier, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, tier, definition.Tiers[tier], ClaudeModelFor);
        TierChoice baseChoice = settings.Choose(Harness, role, "high", definition.Tiers["high"], ClaudeModelFor);
        bool sameModel = choice.Model == baseChoice.Model;
        if (!sameModel)
            SyncSettings.RecordModel(into, Harness, $"{role}-{tier}", choice.Model, choice.Reason);
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string description = SyncWriter.TierDescription(role, tier, sameModel);
        string frontmatter = BuildFrontmatter($"{role}-{tier}", description, choice.Model, choice.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.RenderTierStub(role, tier, Harness, overrideCwd);
        string path = Path.Combine(agentsDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteSkill(string claudeRoot, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        string skillDir = Path.Combine(claudeRoot, "skills", "claustrum");
        if (mode == SyncMode.Write && !root.Escapes(skillDir))
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
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into, root);
    }

    private static string BuildFrontmatter(string name, string description, string model, string effort, string color, string tools, string? disallowedTools)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {SyncWriter.YamlQuoted(description)}\n");
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

    private static readonly string[] readTools = ["Read", "Grep", "Glob", "Bash", "PowerShell", "WebFetch", "WebSearch"];

    private static readonly string[] editTools = ["Read", "Grep", "Glob", "Bash", "PowerShell", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch"];

    // What `shell` denies unless the role asks for it back. Granting and denying the same tool is
    // incoherent, so anything role.json names under `tools` drops out of this list (#29).
    private static readonly string[] shellDenied = ["Agent", "Edit", "Write", "NotebookEdit"];

    private static (string Tools, string? DisallowedTools) ToolsFor(RoleDefinition definition)
    {
        // `withoutTools` is honoured at every rung (#45): a dropped tool leaves `tools` and joins
        // `disallowedTools`, so a tool is never both granted and denied.
        string[] dropped = definition.DroppedTools;
        if (definition.Permission == "readonly")
            return Format(Without(readTools, dropped), ["Agent", "Edit", "Write", .. dropped]);

        // `shell` is read + run, never edit — the rung M3 added for ui-reviewer. Before #29 it fell
        // through to the edit set, so a browser role synced with Edit/Write its own ground rules
        // forbid and no browser tool at all: the one thing it exists for.
        if (definition.Permission == "shell")
        {
            string[] granted = definition.MayDelegate.Length > 0 ? [.. definition.ExtraTools, "Agent"] : definition.ExtraTools;
            string[] denied = [.. shellDenied.Where(tool => !granted.Contains(tool, StringComparer.Ordinal)), .. dropped];
            return Format(Without([.. readTools, .. granted], dropped), denied);
        }

        // The edit rungs add role.json's `tools` too: the demo-author moved here from `shell` (#43) and
        // still needs its browser. A role that delegates to no one gets `Agent` disallowed, as on the
        // two rungs above — listing tools does not take it away (NOTES.md, issue #19).
        // role.json `deny` is NOT rendered into `disallowedTools`, deliberately: there `Bash(git push *)`
        // removes the whole Bash tool (code.claude.com/docs/en/sub-agents, read 2026-10-02). A
        // frontmatter PreToolUse hook could carry it and is a follow-up issue; until then a synced
        // agent's deny list is its prose, and only `claustrum run` enforces it.
        string[] kept = Without(editTools, dropped);
        string[] extra = [.. Without(definition.ExtraTools, dropped).Where(tool => !kept.Contains(tool, StringComparer.Ordinal))];
        string[] tools = definition.MayDelegate.Length > 0 ? [.. kept, .. extra, "Agent"] : [.. kept, .. extra];
        string[] disallowed = definition.MayDelegate.Length > 0 ? dropped : ["Agent", .. dropped];
        return Format(Without(tools, dropped), disallowed);
    }

    private static string[] Without(IEnumerable<string> tools, string[] dropped) =>
        [.. tools.Where(tool => !dropped.Contains(tool, StringComparer.Ordinal))];

    // Distinct: a dropped `Agent` on a role that delegates to no one is denied by the rung and by the role.
    private static (string Tools, string? DisallowedTools) Format(string[] tools, string[] disallowed)
    {
        string[] distinctDisallowed = [.. disallowed.Distinct(StringComparer.Ordinal)];
        return (string.Join(", ", tools.Distinct(StringComparer.Ordinal)), distinctDisallowed.Length > 0 ? string.Join(", ", distinctDisallowed) : null);
    }
}
