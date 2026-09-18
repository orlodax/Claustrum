using System.Security.Cryptography;
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
/// Duplicates ClaudeSync's/OpencodeSync's marker/idempotency machinery rather than sharing it — same
/// "extract once the real common shape is known, not guessed early" reasoning as OpencodeSync's own
/// doc comment.
/// </summary>
public sealed class CopilotSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "copilot";
    private const string MarkerPrefix = "<!-- claustrum:generated";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

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

        List<string> written = [];
        List<string> skipped = [];
        List<string> foreign = [];
        Dictionary<string, string> proposedContent = [];

        foreach (string role in targetRoles)
        {
            LoadedRole loaded = library.LoadRole(role, cwd);
            WriteAgent(agentDir, role, loaded, cwd, force, mode, written, skipped, foreign, proposedContent);

            foreach (string tier in generatedTiers)
                if (loaded.Definition.Tiers.ContainsKey(tier))
                    WriteTierStub(agentDir, role, tier, cwd, force, mode, written, skipped, foreign, proposedContent);
        }

        WriteSkill(githubRoot, force, mode, written, skipped, foreign, proposedContent);

        return new SyncResult(written, skipped, foreign, mode == SyncMode.Write ? null : proposedContent);
    }

    private void WriteAgent(
        string agentDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, Dictionary<string, string> proposedContent)
    {
        string frontmatter = BuildAgentFrontmatter(role, loaded.Definition.Description);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.agent.md");
        WriteGenerated(path, role, frontmatter, body, force, mode, written, skipped, foreign, proposedContent);
    }

    private void WriteTierStub(
        string agentDir, string role, string tier, string cwd, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, Dictionary<string, string> proposedContent)
    {
        string frontmatter = BuildAgentFrontmatter($"{role}-{tier}", TierDescription(role, tier));
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.agent.md");
        WriteGenerated(path, role, frontmatter, body, force, mode, written, skipped, foreign, proposedContent);
    }

    private void WriteSkill(
        string githubRoot, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, Dictionary<string, string> proposedContent)
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
        WriteGenerated(path, "claustrum", frontmatter, body, force, mode, written, skipped, foreign, proposedContent);
    }

    private void WriteGenerated(
        string path, string role, string frontmatter, string body, bool force, SyncMode mode,
        List<string> written, List<string> skipped, List<string> foreign, Dictionary<string, string> proposedContent)
    {
        string trimmedBody = body.Trim();
        string sha256 = ComputeSha256(trimmedBody);
        string marker = $"{MarkerPrefix} role={role} harness={Harness} library={library.Version} sha256={sha256} -->";
        string content = $"{frontmatter.Trim()}\n{marker}\n\n{trimmedBody}\n";

        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path);
            if (NormalizeLineEndings(existing) == NormalizeLineEndings(content))
            {
                skipped.Add(path);
                return;
            }

            if (!HasMarker(existing) && !force)
            {
                foreign.Add(path);
                return;
            }
        }

        if (mode == SyncMode.Write)
            File.WriteAllText(path, content);
        else
            proposedContent[path] = content;

        written.Add(path);
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");

    private static bool HasMarker(string content) =>
        content.Split('\n').Take(15).Any(line => line.TrimEnd('\r').StartsWith(MarkerPrefix, StringComparison.Ordinal));

    private static string ComputeSha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

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

    private static string TierDescription(string role, string tier)
    {
        string capitalized = char.ToUpperInvariant(role[0]) + role[1..];
        return tier switch
        {
            "xhigh" => $"{capitalized} at EXTRA (xhigh) reasoning effort — identical role, model, and rules as the "
                + $"`{role}` agent, but thinks harder. Routine work -> `{role}`; the hardest cases -> `{role}-max`.",
            "max" => $"{capitalized} at MAX reasoning effort — identical role, model, and rules as the `{role}` "
                + "agent, with the deepest reasoning and no token-spend constraint. Reserve for genuinely hard, "
                + $"high-stakes, or previously-stuck cases. For everyday work use `{role}`; for a step up use `{role}-xhigh`.",
            _ => throw new RoleRenderException($"no stub description template for tier '{tier}'"),
        };
    }
}
