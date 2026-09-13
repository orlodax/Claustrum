using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Claustrum.Roles.Json;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.claude/agents/&lt;role&gt;.md` (+ generated `-xhigh`/`-max` tier
/// stubs) and `.claude/skills/delegate/SKILL.md` (docs/PLAN.md §B4). Idempotent by construction: a
/// file without the `claustrum:generated` marker is never overwritten unless `force` is set.
/// </summary>
public sealed class ClaudeSync(RoleLibrary library, RoleRenderer renderer)
{
    private const string Harness = "claude";
    private const string MarkerPrefix = "<!-- claustrum:generated";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    public SyncResult Sync(string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false)
    {
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 } ? roles : library.ListRoles();
        // `--global` targets `~/.claude/...` throughout, not just the agents dir (review finding #3:
        // the skill file used to hardcode `cwd` here regardless of `global`).
        string claudeRoot = global
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Path.Combine(cwd, ".claude");
        string agentsDir = Path.Combine(claudeRoot, "agents");
        Directory.CreateDirectory(agentsDir);

        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        List<SyncManifestFile> manifestFiles = [];

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteBaseAgent(agentsDir, role, loaded, cwd, force, written, skipped, foreign, manifestFiles);

            foreach (string tier in generatedTiers)
            {
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentsDir, role, tier, loaded, cwd, force, written, skipped, foreign, manifestFiles);
            }
        }

        WriteSkill(claudeRoot, force, written, skipped, foreign, manifestFiles);

        if (!global)
            UpdateManifest(cwd, manifestFiles);

        return new SyncResult(written, skipped, foreign);
    }

    private void WriteBaseAgent(
        string agentsDir, string role, LoadedRole loaded, string cwd, bool force,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier tier = definition.Tiers["high"];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string frontmatter = BuildFrontmatter(role, definition.Description, ClaudeModelFor(tier.Model), tier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentsDir, $"{role}.md");
        WriteGenerated(path, role, frontmatter, body, force, written, skipped, foreign, manifestFiles);
    }

    private void WriteTierStub(
        string agentsDir, string role, string tier, LoadedRole loaded, string cwd, bool force,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier roleTier = definition.Tiers[tier];
        (string tools, string? disallowedTools) = ToolsFor(definition);
        string description = TierDescription(role, tier);
        string frontmatter = BuildFrontmatter($"{role}-{tier}", description, ClaudeModelFor(roleTier.Model), roleTier.Effort, definition.Color, tools, disallowedTools);
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentsDir, $"{role}-{tier}.md");
        WriteGenerated(path, role, frontmatter, body, force, written, skipped, foreign, manifestFiles);
    }

    private void WriteSkill(string claudeRoot, bool force, List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles)
    {
        string skillDir = Path.Combine(claudeRoot, "skills", "delegate");
        Directory.CreateDirectory(skillDir);
        string frontmatter = """
            ---
            name: delegate
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend.
            ---
            """;
        string body = """
            # Delegate

            Write the brief to a file first — fixed H2 sections `## Task`, `## Scope`,
            `## Must still work`, `## Diff`, `## Context` (non-blind roles only); see this repo's
            `docs/PLAN.md` §B3 for the exact convention — then invoke:

            ```
            claustrum run <role> --brief-file <path> --json
            ```

            Parse the single JSON document Claustrum prints to stdout for `status`, `changed_files`,
            `diff`, and `report`. When the `claustrum` MCP server is connected, use the `delegate`
            tool instead of the shell command: `{role, brief, cwd?, backend?, model?, effort?,
            permission?, ...}`, still with the brief written to a file first if you already have one.
            """;
        string path = Path.Combine(skillDir, "SKILL.md");
        WriteGenerated(path, "delegate", frontmatter, body, force, written, skipped, foreign, manifestFiles);
    }

    private void WriteGenerated(
        string path, string role, string frontmatter, string body, bool force,
        List<string> written, List<string> skipped, List<string> foreign, List<SyncManifestFile> manifestFiles)
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

        File.WriteAllText(path, content);
        written.Add(path);
        manifestFiles.Add(new SyncManifestFile(path, role, Harness, sha256));
    }

    private void UpdateManifest(string cwd, List<SyncManifestFile> manifestFiles)
    {
        string manifestDir = Path.Combine(cwd, ".claustrum");
        Directory.CreateDirectory(manifestDir);
        string manifestPath = Path.Combine(manifestDir, "sync-manifest.json");

        Dictionary<string, SyncManifestFile> merged = [];
        if (File.Exists(manifestPath))
        {
            SyncManifest? existing = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), RolesJsonContext.Default.SyncManifest);
            foreach (SyncManifestFile file in existing?.Files ?? [])
                merged[file.Path] = file;
        }

        foreach (SyncManifestFile file in manifestFiles)
            merged[file.Path] = file;

        SyncManifest manifest = new(library.Version, [.. merged.Values.OrderBy(f => f.Path, StringComparer.Ordinal)]);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, RolesJsonContext.Default.SyncManifest));
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
