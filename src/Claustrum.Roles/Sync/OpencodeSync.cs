using System.Text;
using System.Text.Json.Nodes;
using Claustrum.Core.Config;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.opencode/agent/&lt;role&gt;.md` (+ generated `-xhigh`/`-max` tier
/// stubs) and `.opencode/command/claustrum.md` — opencode's own analogue of ClaudeSync's
/// `.claude/agents/*.md` + `.claude/skills/claustrum/SKILL.md`, verified against opencode's own
/// bundled "Customizing opencode" reference doc (2026-09-18 — see NOTES.md "OpencodeSync" for exactly
/// what that confirmed). A `command`, not a `skill`, is what makes `/claustrum` an actual typeable
/// slash command in opencode's UI; opencode's own "skill" concept is auto-surfaced reference material,
/// not a slash command, so it would not satisfy docs/PLAN.md §D2's "/claustrum works in ... opencode".
///
/// It also registers the claustrum MCP server in `opencode.json`'s own "mcp" key (issue #15) through
/// the same <see cref="McpConfigSync"/> that handles ClaudeSync's `.mcp.json` — one shared merge, so
/// the idempotency and foreign-key rules are identical on both sides.
///
/// The marker/idempotency machinery lives in <see cref="SyncWriter"/> and the result lists in
/// <see cref="SyncAccumulator"/>, shared with ClaudeSync and CopilotSync — the extraction this
/// comment used to defer until "Cursor/CopilotSync exist too and the real common shape is known".
/// What stays here is what is genuinely per-harness: where files live, and the frontmatter format.
/// </summary>
public sealed class OpencodeSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "opencode";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly SyncWriter writer = new(Harness, library.Version);

    /// <summary>The one place `~/.config/opencode` is spelled, shared by the fallback below and the CLI's resolver.</summary>
    public static string DefaultGlobalRoot(string homeDirectory) => Path.Combine(homeDirectory, ".config", "opencode");

    /// <summary>
    /// <paramref name="globalRoot"/> is the global root the caller already resolved from the
    /// environment (`OPENCODE_CONFIG_DIR`, `XDG_CONFIG_HOME`); null means the default under `homeDirectory`.
    /// <paramref name="config"/> is the merged `claustrum.json` the agents' model and deny follow (#53); null means
    /// the hard-coded defaults.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, string? globalRoot = null, Config? config = null)
    {
        // `--global` renders the library alone: a repo's `.claustrum/roles/` applies to that repo, and its
        // files must not leak into the user-global agents (issue #99).
        string? overrideCwd = global ? null : cwd;

        // Unlike ClaudeSync (where every current role happens to list "claude" and ship an
        // environment.claude.md), ui-reviewer lists only "claude" and has no environment.default.md
        // fallback — rendering it for "opencode" throws by design. The default "sync everything" case
        // has to filter to roles that actually declare opencode support; an explicit --only-roles list
        // naming an unsupported role is left to fail loudly with RoleRenderer's own clear exception.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, overrideCwd).Definition.Harnesses.Contains(Harness))];

        // docs/PLAN.md's own "Where files live" table (opencode's bundled reference doc): project
        // agents/commands live under .opencode/, global ones under ~/.config/opencode/ (NOT
        // ~/.opencode/). The root and homeDirectory are caller-supplied, not read from the
        // environment here, for the same reason ClaudeSync's are (a test can redirect --global away
        // from the real ~/.config).
        string harnessRoot = global
            ? globalRoot ?? DefaultGlobalRoot(homeDirectory)
            : Path.Combine(cwd, ".opencode");
        SyncRoot root = global ? SyncRoot.Global(harnessRoot) : SyncRoot.Repo(cwd);
        string agentDir = Path.Combine(harnessRoot, "agent");
        if (mode == SyncMode.Write && !root.Escapes(agentDir))
            Directory.CreateDirectory(agentDir);

        SyncAccumulator into = new();
        SyncSettings settings = new(config);
        if (!global)
            SyncManifestStore.RefuseIfEscaping(cwd, root, into);

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, overrideCwd);
            if (global && RoleLibrary.HasLocalOverride(role, cwd))
                into.IgnoredOverrides.Add(role);

            WriteAgent(agentDir, role, loaded, settings, overrideCwd, force, mode, into, root);

            foreach (string tier in generatedTiers)
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentDir, role, tier, loaded, settings, overrideCwd, force, mode, into, root);
        }

        WriteCommand(harnessRoot, force, mode, into, root);

        // opencode.json is a repo-root file, not one of §B4's --global targets (agent directories,
        // the desktop app's own config file) — skipped entirely under --global, exactly as
        // ClaudeSync skips `.mcp.json` there, and so is the manifest that proves its provenance.
        if (!global)
            MergeMcpConfig(cwd, mode, force, into, root);

        if (!global && mode == SyncMode.Write)
            SyncManifestStore.Update(cwd, library.Version, into);

        return into.ToResult(mode);
    }

    // Documented shape (opencode.ai/docs/mcp-servers, checked 2026-09-21): top-level "mcp", entry
    // `{"type": "local", "command": ["claustrum", "mcp"]}` — `command` is an ARRAY here, unlike the
    // command+args split `.mcp.json`/`.vscode/mcp.json` use. The optional enabled/environment/
    // timeout/cwd fields are left out so opencode's own defaults apply.
    private static void MergeMcpConfig(string cwd, SyncMode mode, bool force, SyncAccumulator into, SyncRoot root)
    {
        // opencode reads either name, and writing back through JsonNode drops comments (the
        // trade-off McpConfigSync documents), so the `.jsonc` is only targeted when it is the file
        // that actually exists — a repo with neither gets the documented `opencode.json`.
        string jsonPath = Path.Combine(cwd, "opencode.json");
        string jsoncPath = Path.Combine(cwd, "opencode.jsonc");
        string path = !File.Exists(jsonPath) && File.Exists(jsoncPath) ? jsoncPath : jsonPath;

        JsonObject entry = new() { ["type"] = "local", ["command"] = new JsonArray("claustrum", "mcp") };
        // $schema seeds a file created from nothing (it is what opencode's own docs show first) and is
        // never added to — or rewritten in — a config a human already has.
        JsonObject rootOnCreate = new() { ["$schema"] = "https://opencode.ai/config.json" };
        McpConfigSync.Merge(new McpConfigTarget(Harness, path, "mcp", entry, rootOnCreate), mode, force, into, SyncManifestStore.Read(cwd, into), root);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, "high", definition.Tiers["high"], OpencodeModelFor);
        SyncSettings.RecordModel(into, Harness, role, choice.Model, choice.Reason);
        settings.RecordUnrenderedPermission(into, Harness, role);
        string frontmatter = BuildAgentFrontmatter(definition.Description, definition.Primary, choice, definition.Deny);
        string body = renderer.Render(role, "high", Harness, overrideCwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteTierStub(string agentDir, string role, string tier, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, tier, definition.Tiers[tier], OpencodeModelFor);
        bool sameModel = choice.Model == settings.Choose(Harness, role, "high", definition.Tiers["high"], OpencodeModelFor).Model;
        if (!sameModel)
            SyncSettings.RecordModel(into, Harness, $"{role}-{tier}", choice.Model, choice.Reason);
        string frontmatter = BuildAgentFrontmatter(SyncWriter.TierDescription(role, tier, sameModel), definition.Primary, choice, definition.Deny);
        string body = renderer.RenderTierStub(role, tier, Harness, overrideCwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteCommand(string harnessRoot, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        string commandDir = Path.Combine(harnessRoot, "command");
        if (mode == SyncMode.Write && !root.Escapes(commandDir))
            Directory.CreateDirectory(commandDir);

        string frontmatter = """
            ---
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend, and set up a cast (who plays which role) the first time.
            ---
            """;
        string body = library.ReadShared("_shared/claustrum-skill.md");
        string path = Path.Combine(commandDir, "claustrum.md");
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into, root);
    }

    // `variant:` is opt-in (#35, option c): written only when the model spec itself carries `#<variant>`,
    // never from the tier's effort, which a model may not publish. `mode: all` lets a primary role (the
    // architect) be picked as the main agent and still be spawned as a subagent (#85).
    private static string BuildAgentFrontmatter(string description, bool primary, TierChoice choice, string[] roleDeny)
    {
        (string model, string? variant) = choice.Model.Split('#', 2) is [string name, string tag] ? (name, tag) : (choice.Model, null);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(primary ? "mode: all\n" : "mode: subagent\n");
        builder.Append($"description: {SyncWriter.YamlQuoted(description)}\n");
        builder.Append($"model: {model}\n");
        if (variant is not null)
            builder.Append($"variant: {variant}\n");

        // role.json's deny plus claustrum.json's, as the bash permission `run` writes (OpencodeBackend.WriteBashRules):
        // deny entries only, no `"*": allow`, so the block never opens bash for a role whose level closes it.
        // role.json's own `git push` was missing until the #53 review: a synced agent enforced less than `run`.
        string[] deny = [.. roleDeny, .. choice.Deny];
        if (deny.Length > 0)
        {
            builder.Append("permission:\n  bash:\n");
            foreach (string pattern in deny.Distinct(StringComparer.Ordinal))
            {
                builder.Append($"    {SyncWriter.YamlQuoted(pattern)}: deny\n");
                builder.Append($"    {SyncWriter.YamlQuoted($"{pattern} *")}: deny\n");
            }
        }

        builder.Append("---");
        return builder.ToString();
    }

    // Only the two model ids docs/PLAN.md itself ever actually names (line 219's
    // "cheap-coding -> opencode:openrouter/deepseek/deepseek-v4-flash", and the recurring
    // "openrouter/deepseek/deepseek-v4-pro" builder cast example) — a plausible bare "deepseek-v4"
    // for standard-coding was deliberately not invented; every model class maps to one of these two
    // confirmed-real ids instead.
    private static string OpencodeModelFor(string modelClass) => modelClass switch
    {
        "frontier-reasoning" or "frontier-coding" or "standard-coding" => "openrouter/deepseek/deepseek-v4-pro",
        "cheap-coding" or "fast" => "openrouter/deepseek/deepseek-v4-flash",
        _ => throw new RoleRenderException($"no opencode model mapping for class '{modelClass}'"),
    };
}
