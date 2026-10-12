using System.Text;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.github/agents/&lt;role&gt;.agent.md` (+ `-xhigh`/`-max` tier
/// stubs) and `.github/skills/claustrum/SKILL.md` — Copilot CLI's analogue of ClaudeSync's
/// `.claude/agents/*.md` + skill, on <see cref="SyncWriter"/>'s shared marker/idempotency
/// machinery. NOTES.md "CopilotSync: agent + skill files" and "The copilot backend, validated
/// against a real install" hold the why, including `model: auto`.
/// Two receipts that decide code below: Copilot CLI has no command concept at all (skills are its
/// only reusable-instruction mechanism, auto-surfaced by relevance and never typed as `/name`), and
/// `description` is the one required frontmatter key.
/// </summary>
public sealed class CopilotSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "copilot";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly SyncWriter writer = new(Harness, library.Version);

    /// <summary>The one place `~/.copilot` is spelled, shared by the fallback below and the CLI's resolver.</summary>
    public static string DefaultGlobalRoot(string homeDirectory) => Path.Combine(homeDirectory, ".copilot");

    /// <summary>
    /// <paramref name="globalRoot"/> is the global root the caller already resolved from the
    /// environment (`COPILOT_HOME`); null means the default under `homeDirectory`.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, string? globalRoot = null)
    {
        // Same reasoning as OpencodeSync: ui-reviewer lists only "claude" and has no
        // environment.default.md fallback, so the default "sync everything" case has to filter to
        // roles that actually declare copilot support.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, cwd).Definition.Harnesses.Contains(Harness))];

        // Both halves are now measured, not extrapolated: `copilot skill --help` names
        // `~/.copilot/skills/` as the personal skill location, and copilot 1.0.87 really does load
        // `~/.copilot/agents/*.agent.md` — the exact files written here round-tripped through it
        // (2026-09-22, NOTES.md "The copilot backend, validated against a real install").
        string githubRoot = global ? globalRoot ?? DefaultGlobalRoot(homeDirectory) : Path.Combine(cwd, ".github");
        SyncRoot root = global ? SyncRoot.Global(githubRoot) : SyncRoot.Repo(cwd);
        string agentDir = Path.Combine(githubRoot, "agents");
        if (mode == SyncMode.Write && !root.Escapes(agentDir))
            Directory.CreateDirectory(agentDir);

        SyncAccumulator into = new();

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteAgent(agentDir, role, loaded, cwd, force, mode, into, root);

            foreach (string tier in generatedTiers)
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentDir, role, tier, loaded, cwd, force, mode, into, root);
        }

        WriteSkill(githubRoot, force, mode, into, root);

        return into.ToResult(mode);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        string frontmatter = BuildAgentFrontmatter(role, loaded.Definition.Description);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.agent.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteTierStub(string agentDir, string role, string tier, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        // Every copilot stub emits `model: auto`, so the model never differs from the base agent's (#30).
        string frontmatter = BuildAgentFrontmatter($"{role}-{tier}", SyncWriter.TierDescription(role, tier, sameModel: true));
        string body = renderer.RenderTierStub(role, tier, Harness, cwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.agent.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteSkill(string githubRoot, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        string skillDir = Path.Combine(githubRoot, "skills", "claustrum");
        if (mode == SyncMode.Write && !root.Escapes(skillDir))
            Directory.CreateDirectory(skillDir);

        // No slash-command mechanism exists (class doc comment), so the description front-loads
        // trigger phrasing the way copilot's own built-in skills do ("Use when...").
        string frontmatter = """
            ---
            name: claustrum
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend, and set up a cast (who plays which role) the first time. Use when the user asks to delegate a task, run a builder/reviewer/tester, or set up/change a Claustrum cast.
            ---
            """;
        string body = library.ReadShared("_shared/claustrum-skill.md");
        string path = Path.Combine(skillDir, "SKILL.md");
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into, root);
    }

    private static string BuildAgentFrontmatter(string name, string description)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {SyncWriter.YamlQuoted(description)}\n");
        builder.Append("model: auto\n");
        builder.Append("---");
        return builder.ToString();
    }
}
