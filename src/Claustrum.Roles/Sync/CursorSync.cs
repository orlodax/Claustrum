using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Nodes;
using Claustrum.Core.Config;
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

    /// <summary>The one place `~/.cursor` is spelled, shared by the fallback below and the CLI's resolver.</summary>
    public static string DefaultGlobalRoot(string homeDirectory) => Path.Combine(homeDirectory, ".cursor");

    /// <summary>
    /// <paramref name="globalRoot"/> is the global root the caller already resolved (null means the
    /// default under `homeDirectory`); the CLI always passes `~/.cursor`, see NOTES.md
    /// "`sync --global` roots follow each harness's config-dir variable".
    /// <paramref name="globalBinaryPath"/> is the absolute <c>claustrum</c> binary to register in
    /// <c>~/.cursor/mcp.json</c> under <paramref name="global"/> — resolved by the caller, as
    /// <see cref="ClaudeDesktopTarget.BinaryPath"/> is. Null skips that one merge: a caller that is
    /// not the binary (a `dotnet run`) has nothing honest to write there.
    /// <paramref name="config"/> is the merged `claustrum.json` the agents' model follows (#53); null means `inherit`.
    /// </summary>
    public SyncResult Sync(
        string cwd, IReadOnlyList<string>? roles = null, bool global = false, bool force = false,
        SyncMode mode = SyncMode.Write, string? globalBinaryPath = null,
        string? globalRoot = null, Config? config = null)
    {
        // `--global` renders the library alone: a repo's `.claustrum/roles/` applies to that repo, and its
        // files must not leak into the user-global agents (issue #99).
        string? overrideCwd = global ? null : cwd;

        // Same reasoning as Opencode/CopilotSync: ui-reviewer lists only "claude" and has no
        // environment.default.md fallback, so the default "sync everything" case has to filter to
        // roles that actually declare cursor support.
        IReadOnlyList<string> targetRoles = roles is { Count: > 0 }
            ? roles
            : [.. library.ListRoles().Where(role => library.LoadRole(role, overrideCwd).Definition.Harnesses.Contains(Harness))];

        // Cursor documents the same three names under `.cursor/` and `~/.cursor/`, mcp.json included
        // — so unlike claude/opencode nothing here is repo-root-only and --global writes all three.
        string cursorRoot = global ? globalRoot ?? DefaultGlobalRoot(homeDirectory) : Path.Combine(cwd, ".cursor");
        SyncRoot root = global ? SyncRoot.Global(cursorRoot) : SyncRoot.Repo(cwd);
        string agentDir = Path.Combine(cursorRoot, "agents");
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

        WriteSkill(cursorRoot, force, mode, into, root);
        MergeMcpConfig(cursorRoot, cwd, global, globalBinaryPath, mode, force, into, root);

        if (!global && mode == SyncMode.Write)
            SyncManifestStore.Update(cwd, library.Version, into);

        return into.ToResult(mode);
    }

    private void WriteAgent(string agentDir, string role, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, "high", definition.Tiers["high"], DefaultModel);
        SyncSettings.RecordModel(into, Harness, role, choice.Model, choice.Reason);
        SyncSettings.RecordUnrenderedDeny(into, Harness, role, choice);
        string frontmatter = BuildAgentFrontmatter(role, definition.Description, definition.Permission, choice.Model);
        string body = renderer.Render(role, "high", Harness, overrideCwd).SystemBody;
        string path = Path.Combine(agentDir, $"{role}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteTierStub(string agentDir, string role, string tier, LoadedRole loaded, SyncSettings settings, string? overrideCwd, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        RoleDefinition definition = loaded.Definition;
        TierChoice choice = settings.Choose(Harness, role, tier, definition.Tiers[tier], DefaultModel);
        bool sameModel = choice.Model == settings.Choose(Harness, role, "high", definition.Tiers["high"], DefaultModel).Model;
        if (!sameModel)
            SyncSettings.RecordModel(into, Harness, $"{role}-{tier}", choice.Model, choice.Reason);
        string frontmatter = BuildAgentFrontmatter($"{role}-{tier}", SyncWriter.TierDescription(role, tier, sameModel), definition.Permission, choice.Model);
        string body = renderer.RenderTierStub(role, tier, Harness, overrideCwd);
        string path = Path.Combine(agentDir, $"{role}-{tier}.md");
        writer.Write(path, role, frontmatter, body, force, mode, into, root, localOverride: loaded.IsLocalOverride);
    }

    private void WriteSkill(string cursorRoot, bool force, SyncMode mode, SyncAccumulator into, SyncRoot root)
    {
        // The directory name has to match the skill's `name` (cursor.com/docs/context/skills).
        string skillDir = Path.Combine(cursorRoot, "skills", "claustrum");
        if (mode == SyncMode.Write && !root.Escapes(skillDir))
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
        writer.Write(path, "claustrum", frontmatter, body, force, mode, into, root);
    }

    // Documented shape (cursor.com/docs/context/mcp, checked 2026-09-22): top-level "mcpServers",
    // entry `{"command": …, "args": […]}` — the same entry `.mcp.json` gets. `~/.cursor/mcp.json` is
    // a real user-level target rather than a repo file, so --global writes it too; no manifest exists
    // out there, hence McpProvenance.OwnKey (the key's own name is its provenance).
    private static void MergeMcpConfig(
        string cursorRoot, string cwd, bool global, string? globalBinaryPath, SyncMode mode, bool force, SyncAccumulator into, SyncRoot root)
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
            Harness, Path.Combine(cursorRoot, "mcp.json"), "mcpServers", entry,
            Provenance: global ? McpProvenance.OwnKey : McpProvenance.Manifest);
        IReadOnlyDictionary<string, SyncManifestFile> manifest = global
            ? ReadOnlyDictionary<string, SyncManifestFile>.Empty
            : SyncManifestStore.Read(cwd, into);
        McpConfigSync.Merge(target, mode, force, into, manifest, root);
    }

    // `inherit` is the field's documented default and the only value that works on a Free plan, which
    // refuses named model ids outright (NOTES.md "The cursor backend, validated against a real install"):
    // only a `cursor:<id>` alias in claustrum.json names one (#53), so a tier otherwise lives in the body.
    private static string DefaultModel(string _) => "inherit";

    private static string BuildAgentFrontmatter(string name, string description, string permission, string model)
    {
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append($"name: {name}\n");
        builder.Append($"description: {SyncWriter.YamlQuoted(description)}\n");
        builder.Append($"model: {model}\n");
        // `readonly: true` is cursor's own restricted-write rung; false is the default, so it is
        // written only where the role asks for it.
        if (permission == "readonly")
            builder.Append("readonly: true\n");
        builder.Append("---");
        return builder.ToString();
    }
}
