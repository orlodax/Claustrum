using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Claustrum.Roles.Json;
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
    private const string MarkerPrefix = "<!-- claustrum:generated";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    public SyncResult Sync(string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false, SyncMode mode = SyncMode.Write)
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

        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        List<SyncManifestFile> manifestFiles = [];
        Dictionary<string, string> proposedContent = [];

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteBaseAgent(agentsDir, role, loaded, cwd, force, mode, written, skipped, foreign, manifestFiles, proposedContent);

            foreach (string tier in generatedTiers)
            {
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentsDir, role, tier, loaded, cwd, force, mode, written, skipped, foreign, manifestFiles, proposedContent);
            }
        }

        WriteSkill(claudeRoot, force, mode, written, skipped, foreign, manifestFiles, proposedContent);

        // .mcp.json/.vscode/mcp.json (docs/PLAN.md §B4/§D5) are repo-root files, not part of any
        // --global target (B4's --global list is agent directories and the desktop app's own config
        // file) — skipped entirely under --global, same as the manifest itself.
        if (!global)
            McpConfigSync.Sync(cwd, mode, force, written, skipped, foreign, manifestFiles, proposedContent, ReadManifest(cwd));

        if (!global && mode == SyncMode.Write)
            UpdateManifest(cwd, manifestFiles);

        return new SyncResult(written, skipped, foreign, mode == SyncMode.Write ? null : proposedContent);
    }

    private void WriteBaseAgent(
        string agentsDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles, Dictionary<string, string> proposedContent)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier tier = definition.Tiers["high"];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string frontmatter = BuildFrontmatter(role, definition.Description, ClaudeModelFor(tier.Model), tier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentsDir, $"{role}.md");
        WriteGenerated(path, role, frontmatter, body, force, mode, written, skipped, foreign, manifestFiles, proposedContent);
    }

    private void WriteTierStub(
        string agentsDir, string role, string tier, LoadedRole loaded, string cwd, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles, Dictionary<string, string> proposedContent)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier roleTier = definition.Tiers[tier];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string description = TierDescription(role, tier);
        string frontmatter = BuildFrontmatter($"{role}-{tier}", description, ClaudeModelFor(roleTier.Model), roleTier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentsDir, $"{role}-{tier}.md");
        WriteGenerated(path, role, frontmatter, body, force, mode, written, skipped, foreign, manifestFiles, proposedContent);
    }

    private void WriteSkill(
        string claudeRoot, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles, Dictionary<string, string> proposedContent)
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
        string body = """
            # Claustrum

            ## First time in this repo (or asked to set up/change a cast)
            Run `claustrum cast questions --json` (or the MCP `cast_questions` tool) and ask the user
            each question with your host's native question mechanism (e.g. `AskUserQuestion` in
            Claude Code). Write the answers to a file keyed by each question's `key`, then
            `claustrum cast create --answers <file>` (or the MCP `cast_create` tool). The identical
            questions are asked in every harness this library supports; only the picker fidelity
            differs.

            ## Delegating
            Write the brief to a file first — fixed H2 sections `## Task`, `## Scope`,
            `## Must still work`, `## Diff`, `## Context` (non-blind roles only); see this repo's
            `docs/PLAN.md` §B3 for the exact convention — then invoke:

            ```
            claustrum run <role> --brief-file <path> --json [--cast <name>]
            ```

            Parse the single JSON document Claustrum prints to stdout for `status`, `changed_files`,
            `diff`, and `report`. When the `claustrum` MCP server is connected, use the `delegate`
            tool instead of the shell command: `{role, brief, cwd?, backend?, model?, effort?, tier?,
            permission?, cast?, ...}`, still with the brief written to a file first if you already
            have one. With no `--cast`/`cast` given, a repo's `.claustrum/casts/default.json` applies
            itself automatically if present.
            """;
        string path = Path.Combine(skillDir, "SKILL.md");
        WriteGenerated(path, "claustrum", frontmatter, body, force, mode, written, skipped, foreign, manifestFiles, proposedContent);
    }

    private void WriteGenerated(
        string path, string role, string frontmatter, string body, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles, Dictionary<string, string> proposedContent)
    {
        string trimmedBody = body.Trim();
        string sha256 = ComputeSha256(trimmedBody);
        string marker = $"{MarkerPrefix} role={role} harness={Harness} library={library.Version} sha256={sha256} -->";
        string content = $"{frontmatter.Trim()}\n{marker}\n\n{trimmedBody}\n";

        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);

            // Compare with line endings normalized: a CRLF checkout (core.autocrlf) makes
            // File.ReadAllText return `\r\n` while `content` above is built with plain `\n`, so a
            // byte-exact compare here rewrote every generated file on every `sync` (review finding #3).
            if (NormalizeLineEndings(existing) == NormalizeLineEndings(content))
            {
                skipped.Add(path);
                manifestFiles.Add(new SyncManifestFile(path, role, Harness, sha256));
                return;
            }

            if (!HasMarker(existing) && !force)
            {
                foreign.Add(path);
                return;
            }
        }

        // SyncMode.DryRun/Check never touch disk: the content that would have been written is kept
        // for the CLI to diff instead (SyncResult.ProposedContent).
        if (mode == SyncMode.Write)
            File.WriteAllText(path, content);
        else
            proposedContent[path] = content;

        written.Add(path);
        manifestFiles.Add(new SyncManifestFile(path, role, Harness, sha256));
    }

    private void UpdateManifest(string cwd, List<SyncManifestFile> manifestFiles)
    {
        Dictionary<string, SyncManifestFile> merged = ReadManifest(cwd);
        foreach (SyncManifestFile file in manifestFiles)
            merged[file.Path] = file;

        string manifestDir = Path.Combine(cwd, ".claustrum");
        Directory.CreateDirectory(manifestDir);
        SyncManifest manifest = new(library.Version, [.. merged.Values.OrderBy(f => f.Path, StringComparer.Ordinal)]);
        File.WriteAllText(Path.Combine(manifestDir, "sync-manifest.json"), JsonSerializer.Serialize(manifest, RolesJsonContext.Default.SyncManifest));
    }

    // McpConfigSync's only way to tell "claustrum wrote this JSON key last time, safe to overwrite"
    // apart from "a human put unrelated content there" — JSON has no room for the inline
    // claustrum:generated marker WriteGenerated's Markdown targets carry (SyncManifestFile doc
    // comment "so a future non-marker target ... is still idempotent").
    private static Dictionary<string, SyncManifestFile> ReadManifest(string cwd)
    {
        string manifestPath = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        if (!File.Exists(manifestPath))
            return [];

        SyncManifest? existing = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), RolesJsonContext.Default.SyncManifest);
        Dictionary<string, SyncManifestFile> byPath = [];
        foreach (SyncManifestFile file in existing?.Files ?? [])
            byPath[file.Path] = file;

        return byPath;
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static bool HasMarker(string content) =>
        content.Split('\n').Take(15).Any(line => line.TrimEnd('\r').StartsWith(MarkerPrefix, StringComparison.Ordinal));

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

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

    private static string TierDescription(string role, string tier)
    {
        string capitalized = char.ToUpperInvariant(role[0]) + role[1..];
        return tier switch
        {
            "xhigh" => $"{capitalized} at EXTRA (xhigh) reasoning effort — identical role, model, and rules as the "
                + $"`{role}` agent, but thinks harder. Routine work → `{role}`; the hardest cases → `{role}-max`.",
            "max" => $"{capitalized} at MAX reasoning effort — identical role, model, and rules as the `{role}` "
                + "agent, with the deepest reasoning and no token-spend constraint. Reserve for genuinely hard, "
                + $"high-stakes, or previously-stuck cases. For everyday work use `{role}`; for a step up use `{role}-xhigh`.",
            _ => throw new RoleRenderException($"no stub description template for tier '{tier}'"),
        };
    }
}
