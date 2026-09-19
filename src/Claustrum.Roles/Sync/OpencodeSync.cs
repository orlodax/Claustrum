using System.Text;
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
/// Deliberately does not touch opencode.json's own "mcp" key (registering the claustrum MCP server
/// for opencode's native session) — that merge has the same idempotency/foreign-key concerns
/// McpConfigSync solved for `.mcp.json`, deserves the same dedicated care, and is out of scope here.
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

    public SyncResult Sync(string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false, SyncMode mode = SyncMode.Write)
    {
        // Unlike ClaudeSync (where every current role happens to list "claude" and ship an
        // environment.claude.md), ui-reviewer lists only "claude" and has no environment.default.md
        // fallback — rendering it for "opencode" throws by design. The default "sync everything" case
        // has to filter to roles that actually declare opencode support; an explicit --only-roles list
        // naming an unsupported role is left to fail loudly with RoleRenderer's own clear exception.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, cwd).Definition.Harnesses.Contains(Harness))];

        // docs/PLAN.md's own "Where files live" table (opencode's bundled reference doc): project
        // agents/commands live under .opencode/, global ones under ~/.config/opencode/ (NOT
        // ~/.opencode/). homeDirectory is caller-supplied, not read from the environment here,
        // for the same reason ClaudeSync's own homeDirectory is (a test can redirect --global away
        // from the real ~/.config).
        string root = global
            ? Path.Combine(homeDirectory, ".config", "opencode")
            : Path.Combine(cwd, ".opencode");
        string agentDir = Path.Combine(root, "agent");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(agentDir);

        SyncAccumulator into = new();

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteAgent(agentDir, role, loaded, cwd, force, mode, into);

            foreach (string tier in generatedTiers)
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentDir, role, tier, loaded, cwd, force, mode, into);
        }

        WriteCommand(root, force, mode, into);

        return into.ToResult(mode);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        RoleDefinition definition = loaded.Definition;
        RoleTier tier = definition.Tiers["high"];
        string frontmatter = BuildAgentFrontmatter(definition.Description, OpencodeModelFor(tier.Model), tier.Effort);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteTierStub(string agentDir, string role, string tier, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        RoleTier roleTier = loaded.Definition.Tiers[tier];
        string frontmatter = BuildAgentFrontmatter(SyncWriter.TierDescription(role, tier), OpencodeModelFor(roleTier.Model), roleTier.Effort);
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteCommand(string root, bool force, SyncMode mode, SyncAccumulator into)
    {
        string commandDir = Path.Combine(root, "command");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(commandDir);

        string frontmatter = """
            ---
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend, and set up a cast (who plays which role) the first time.
            ---
            """;
        string body = library.ReadShared("_shared/claustrum-skill.md");
        string path = Path.Combine(commandDir, "claustrum.md");
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into);
    }

    private static string BuildAgentFrontmatter(string description, string model, string variant)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append("mode: subagent\n");
        builder.Append($"description: {description}\n");
        builder.Append($"model: {model}\n");
        builder.Append($"variant: {variant}\n");
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
