using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Sync;

/// <summary>
/// Renders the role library into `.cursor/agents/&lt;role&gt;.md` (+ generated `-xhigh`/`-max` tier
/// stubs), `.cursor/skills/claustrum/SKILL.md` and `.cursor/mcp.json` — docs/PLAN.md §B4's cursor
/// row (issue #25). Every frontmatter field is taken from Cursor's own current docs and from the
/// `create-subagent`/`create-skill` skills the installed CLI ships, never guessed (2026-09-22 — see
/// NOTES.md "CursorSync" for what each source confirmed and what stayed unverified).
///
/// A Cursor skill is both slash-invocable (`/claustrum`) and auto-surfaced by its description, so
/// this harness needs no `command`-vs-`skill` split the way opencode did.
///
/// Marker/idempotency machinery comes from <see cref="SyncWriter"/>, the result lists from
/// <see cref="SyncAccumulator"/> and the JSON merge from <see cref="McpConfigSync"/>, shared with the
/// other three harnesses; only the file layout and the frontmatter format are cursor's own.
/// </summary>
public sealed class CursorSync(RoleLibrary library, RoleRenderer renderer, string homeDirectory)
{
    private const string Harness = "cursor";
    private static readonly string[] generatedTiers = ["xhigh", "max"];

    private readonly SyncWriter writer = new(Harness, library.Version);

    /// <summary>
    /// <paramref name="globalBinaryPath"/> is the absolute <c>claustrum</c> binary to register in
    /// <c>~/.cursor/mcp.json</c> under <paramref name="global"/> — resolved by the caller, as
    /// <see cref="ClaudeDesktopTarget.BinaryPath"/> is. Null skips that one merge: a caller that is
    /// not the binary (a `dotnet run`) has nothing honest to write there.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, string? globalBinaryPath = null)
    {
        // Same reasoning as Opencode/CopilotSync: ui-reviewer lists only "claude" and has no
        // environment.default.md fallback, so the default "sync everything" case has to filter to
        // roles that actually declare cursor support.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, cwd).Definition.Harnesses.Contains(Harness))];

        // Cursor documents the same three names under `.cursor/` and `~/.cursor/`, mcp.json included
        // — so unlike claude/opencode nothing here is repo-root-only and --global writes all three.
        string root = global ? Path.Combine(homeDirectory, ".cursor") : Path.Combine(cwd, ".cursor");
        string agentDir = Path.Combine(root, "agents");
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

        WriteSkill(root, force, mode, into);
        MergeMcpConfig(root, cwd, global, globalBinaryPath, mode, force, into);

        if (!global && mode == SyncMode.Write)
            SyncManifestStore.Update(cwd, library.Version, into.ManifestFiles);

        return into.ToResult(mode);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        RoleDefinition definition = loaded.Definition;
        string frontmatter = BuildAgentFrontmatter(role, definition.Description, definition.Permission);
        string body = renderer.Render(role, "high", Harness, cwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteTierStub(string agentDir, string role, string tier, LoadedRole loaded, string cwd, bool force, SyncMode mode, SyncAccumulator into)
    {
        string frontmatter = BuildAgentFrontmatter($"{role}-{tier}", SyncWriter.TierDescription(role, tier), loaded.Definition.Permission);
        string body = renderer.RenderTierStub(role, tier, cwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into);
    }

    private void WriteSkill(string root, bool force, SyncMode mode, SyncAccumulator into)
    {
        // The directory name has to match the skill's `name` (cursor.com/docs/context/skills).
        string skillDir = Path.Combine(root, "skills", "claustrum");
        if (mode == SyncMode.Write)
            Directory.CreateDirectory(skillDir);

        // No `disable-model-invocation`: leaving it out keeps the skill auto-surfaced by description,
        // as on every other harness, and `/claustrum` works either way.
        string frontmatter = """
            ---
            name: claustrum
            description: Delegate a task to a Claustrum role (builder, code-reviewer, ...) running on any configured backend, and set up a cast (who plays which role) the first time.
            ---
            """;
        string body = library.ReadShared("_shared/claustrum-skill.md");
        string path = Path.Combine(skillDir, "SKILL.md");
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into);
    }

    // Documented shape (cursor.com/docs/context/mcp, checked 2026-09-22): top-level "mcpServers",
    // entry `{"command": …, "args": […]}` — the same entry `.mcp.json` gets. `~/.cursor/mcp.json` is
    // a real user-level target rather than a repo file, so --global writes it too; no manifest exists
    // out there, hence McpProvenance.OwnKey (the key's own name is its provenance).
    private static void MergeMcpConfig(
        string root, string cwd, bool global, string? globalBinaryPath, SyncMode mode, bool force, SyncAccumulator into)
    {
        // The user-level file gets the absolute binary, the repo file the bare name: a GUI Cursor's
        // PATH rarely holds the install directory (issue #24's hazard, next door), while
        // `.cursor/mcp.json` is committed and a machine-specific path in it is wrong for everyone
        // else. With no binary to name, the global merge is skipped rather than registering the
        // dotnet host under the `claustrum` key.
        string command;
        if (!global)
            command = "claustrum";
        else if (globalBinaryPath is { Length: > 0 } binary)
            command = binary;
        else
            return;

        JsonObject entry = new() { ["command"] = command, ["args"] = new JsonArray("mcp") };
        McpConfigTarget target = new(
            Harness, Path.Combine(root, "mcp.json"), "mcpServers", entry,
            Provenance: global ? McpProvenance.OwnKey : McpProvenance.Manifest);
        IReadOnlyDictionary<string, SyncManifestFile> manifest = global
            ? ReadOnlyDictionary<string, SyncManifestFile>.Empty
            : SyncManifestStore.Read(cwd);
        McpConfigSync.Merge(target, mode, force, into, manifest);
    }

    private static string BuildAgentFrontmatter(string name, string description, string permission)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {description}\n");
        // `inherit` is the field's documented default and the only value that works on a Free plan,
        // which refuses named model ids outright (NOTES.md "The cursor backend, validated against a
        // real install") — so a role's tier lives in its body here, not in this field.
        builder.Append("model: inherit\n");
        // `readonly: true` is cursor's own restricted-write rung; false is the default, so it is
        // written only where the role asks for it.
        if (permission == "readonly")
            builder.Append("readonly: true\n");
        builder.Append("---");
        return builder.ToString();
    }
}
