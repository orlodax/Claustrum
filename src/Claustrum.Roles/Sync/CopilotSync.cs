using System.Text;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.github/agents/&lt;role&gt;.agent.md` (+ generated `-xhigh`/`-max`
/// tier stubs) and `.github/skills/claustrum/SKILL.md` — Copilot CLI's own analogue of ClaudeSync's
/// `.claude/agents/*.md` + skill (2026-09-18 — see NOTES.md "CopilotSync" for exactly what
/// `copilot --help`/`copilot skill --help` confirmed against a real @github/copilot 1.0.86 install).
///
/// Unlike opencode, Copilot CLI has no distinct "command" concept: its skill mechanism (`copilot
/// skill list`, confirmed live) is the *only* reusable-instruction mechanism it has, and skills are
/// auto-surfaced by relevance ("Use when the user mentions...", same as Claude Code's own skill
/// semantics), not slash-invoked. So `/claustrum` cannot be made a literal typeable command in this
/// harness the way it can in Claude Code or opencode — this writes a skill whose description
/// front-loads the trigger phrasing instead, a genuine, confirmed limitation of this harness rather
/// than an oversight.
///
/// Model mapping is deliberately just `auto` (`copilot --help`'s own "use 'auto' to let Copilot pick
/// automatically"): only one real model id (`gpt-5.4`, from a --help example) was ever confirmed, not
/// a tier catalog, so no model class -> id table was invented the way ClaudeSync's/OpencodeSync's are.
///
/// Marker/idempotency machinery comes from <see cref="SyncWriter"/> and the result lists from
/// <see cref="SyncAccumulator"/>, shared with ClaudeSync and OpencodeSync; only the file layout and
/// the frontmatter format are this harness's own.
/// </summary>
public sealed class CopilotSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "copilot";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly SyncWriter writer = new(Harness, library.Version);

    public SyncResult Sync(string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false, SyncMode mode = SyncMode.Write)
    {
        // Same reasoning as OpencodeSync: ui-reviewer lists only "claude" and has no
        // environment.default.md fallback, so the default "sync everything" case has to filter to
        // roles that actually declare copilot support.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, cwd).Definition.Harnesses.Contains(Harness))];

        // `copilot skill --help`'s own text confirms the personal skill location
        // (`~/.copilot/skills/`); the personal *agent* location is an unconfirmed extrapolation from
        // that same convention (only the project `.github/agents/` location is directly confirmed,
        // via `--add-dir`'s own help text).
        string githubRoot = global ? Path.Combine(homeDirectory, ".copilot") : Path.Combine(cwd, ".github");
        string agentDir = Path.Combine(githubRoot, "agents");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(agentDir);

        SyncAccumulator into = new();

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteAgent(agentDir, role, loaded, cwd, force, mode, into);

            foreach (string tier in generatedTiers)
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentDir, role, tier, cwd, force, mode, into);
        }

        WriteSkill(githubRoot, force, mode, into);

        return into.ToResult(mode);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        string frontmatter = BuildAgentFrontmatter(role, loaded.Definition.Description);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.agent.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteTierStub(string agentDir, string role, string tier, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        string frontmatter = BuildAgentFrontmatter($"{role}-{tier}", SyncWriter.TierDescription(role, tier));
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.agent.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteSkill(string githubRoot, bool force, SyncMode mode, SyncAccumulator into)
    {
        string skillDir = Path.Combine(githubRoot, "skills", "claustrum");
        if (mode == SyncMode.Write)
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
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into);
    }

    private static string BuildAgentFrontmatter(string name, string description)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {description}\n");
        builder.Append("model: auto\n");
        builder.Append("---");
        return builder.ToString();
    }
}
