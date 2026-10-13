using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Claustrum.Roles.Json;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles;

/// <summary>
/// Loads the embedded `roles/` library (docs/PLAN.md §B2), with `&lt;cwd&gt;/.claustrum/roles/&lt;role&gt;/`
/// overriding a role's own files: `ROLE.md` and each `parts/` file replace theirs, `role.json` is
/// deep-merged. `_shared/` (house rules, report formats, tier stub) is never overridable.
/// </summary>
public sealed class RoleLibrary
{
    private readonly Assembly assembly = typeof(RoleLibrary).Assembly;
    private readonly string[] resourceNames;

    public RoleLibrary()
    {
        resourceNames = assembly.GetManifestResourceNames();
        RoleLibraryManifest manifest = ReadEmbeddedJson("library.json", RolesJsonContext.Default.RoleLibraryManifest);
        Version = manifest.Version;
        Classes = manifest.Classes;
        Tiers = manifest.Tiers;
    }

    public string Version { get; }
    public IReadOnlyList<string> Classes { get; }
    public IReadOnlyList<string> Tiers { get; }

    /// <summary>
    /// Every role that ships a `role.json` in the embedded library. Read from each role.json's own
    /// `name` field, not from the resource path — MSBuild mangles `-` to `_` in manifest resource
    /// names, so `code-reviewer/role.json` shows up as `...code_reviewer.role.json`.
    /// </summary>
    public IReadOnlyList<string> ListRoles()
    {
        List<string> roles = [];
        foreach (string resourceName in resourceNames)
        {
            if (!resourceName.EndsWith(".role.json", StringComparison.Ordinal))
                continue;

            using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
            using StreamReader reader = new(stream);
            JsonNode? roleJson = JsonNode.Parse(reader.ReadToEnd());
            if (roleJson?["name"]?.GetValue<string>() is string name)
                roles.Add(name);
        }

        return [.. roles.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The role as the library defines it, with <paramref name="cwd"/>'s `.claustrum/roles/&lt;role&gt;/` laid
    /// over it. A null <paramref name="cwd"/> means the library alone: no local override is looked for or
    /// read (`sync --global`, #99). An override file is read only when its real path stays inside
    /// <paramref name="cwd"/>, leaf link included; one that leaves it throws <see cref="RoleRenderException"/>
    /// (#97). A link that stays inside is read through.
    /// </summary>
    public LoadedRole LoadRole(string role, string? cwd)
    {
        string embeddedJson = ReadEmbedded($"{role}/role.json");
        JsonNode baseNode = JsonNode.Parse(embeddedJson)
            ?? throw new RoleRenderException($"role '{role}': embedded role.json parsed to null");

        string localRoleJsonPath = "";
        string? localRoleJson = null;
        string? localRoleMd = null;
        bool isLocalOverride = false;
        if (cwd is not null)
        {
            localRoleJsonPath = LocalPath(cwd, role, "role.json");
            localRoleJson = ReadLocal(cwd, role, localRoleJsonPath);
            localRoleMd = ReadLocal(cwd, role, LocalPath(cwd, role, "ROLE.md"));
            isLocalOverride = HasLocalOverride(role, cwd);
        }

        RoleDefinition definition;
        try
        {
            JsonNode mergedNode = baseNode;
            if (localRoleJson is not null)
            {
                JsonNode overrideNode = JsonNode.Parse(localRoleJson)
                    ?? throw new RoleRenderException($"role '{role}': '{localRoleJsonPath}' parsed to null");
                mergedNode = DeepMerge(baseNode, overrideNode);
            }

            definition = JsonSerializer.Deserialize(mergedNode.ToJsonString(), RolesJsonContext.Default.RoleDefinition)
                ?? throw new RoleRenderException($"role '{role}': merged role.json deserialized to null");
        }
        catch (JsonException ex) when (localRoleJson is not null)
        {
            // The embedded role.json is pinned by RoleLibraryInvariantTests, so a parse or type error
            // here is the local file's; without its path the CLI printed a bare `error:` and exit 1.
            throw new RoleRenderException($"role '{role}': '{localRoleJsonPath}': {ex.Message}");
        }

        return new LoadedRole(definition, localRoleMd ?? ReadEmbedded($"{role}/ROLE.md"), isLocalOverride);
    }

    /// <summary>
    /// True when <paramref name="cwd"/> holds a local override of <paramref name="role"/>: a `role.json`, a
    /// `ROLE.md`, or any file under `parts/`. Existence only, nothing is read, so the #97 fence does not apply.
    /// </summary>
    public static bool HasLocalOverride(string role, string cwd)
    {
        string partsDir = LocalPath(cwd, role, "parts");
        return File.Exists(LocalPath(cwd, role, "role.json")) || File.Exists(LocalPath(cwd, role, "ROLE.md"))
            || (Directory.Exists(partsDir) && Directory.EnumerateFiles(partsDir).Any());
    }

    /// <summary>Reads a `_shared/...` file (house rules, report format, tier stub template).</summary>
    public string ReadShared(string relativePath) => ReadEmbedded(relativePath);

    /// <summary>
    /// Resolves `{{part:name}}`: local override wins over embedded, harness-specific file wins over
    /// `.default.md` (docs/PLAN.md §B2). A null <paramref name="cwd"/> reads the library alone, and a local
    /// part whose real path leaves <paramref name="cwd"/> throws <see cref="RoleRenderException"/>, as in
    /// <see cref="LoadRole"/> (#99, #97).
    /// </summary>
    public string ReadPart(string role, string partName, string harness, string? cwd)
    {
        string harnessRel = $"{role}/parts/{partName}.{harness}.md";
        string defaultRel = $"{role}/parts/{partName}.default.md";

        if (cwd is not null)
        {
            foreach (string rel in new[] { harnessRel, defaultRel })
            {
                string localPath = Path.Combine(cwd, ".claustrum", "roles", rel.Replace('/', Path.DirectorySeparatorChar));
                if (ReadLocal(cwd, role, localPath) is string localPart)
                    return localPart;
            }
        }

        foreach (string rel in new[] { harnessRel, defaultRel })
        {
            if (EmbeddedExists(rel))
                return ReadEmbedded(rel);
        }

        throw new RoleRenderException($"role '{role}': no part '{partName}' for harness '{harness}' (looked for {harnessRel} and {defaultRel})");
    }

    private static string LocalPath(string cwd, string role, string relative) => Path.Combine(cwd, ".claustrum", "roles", role, relative);

    // The one place an override's bytes are read, so the fence cannot be skipped by a new call site. The
    // SyncRoot is built only for a file that exists: the common no-override case pays nothing (#97).
    private static string? ReadLocal(string cwd, string role, string path)
    {
        if (!File.Exists(path))
            return null;

        if (SyncRoot.Repo(cwd).Escapes(path))
            throw new RoleRenderException($"role '{role}': local override '{path}' resolves outside the working directory ({SyncRoot.Resolve(path)}); an override is never read through a link that leaves it");

        return File.ReadAllText(path);
    }

    private static JsonNode DeepMerge(JsonNode baseNode, JsonNode overrideNode)
    {
        if (baseNode is JsonObject baseObject && overrideNode is JsonObject overrideObject)
        {
            JsonObject merged = [];
            foreach (KeyValuePair<string, JsonNode?> property in baseObject)
                merged[property.Key] = property.Value?.DeepClone();

            foreach (KeyValuePair<string, JsonNode?> property in overrideObject)
            {
                merged[property.Key] = property.Value is JsonObject
                    && merged.TryGetPropertyValue(property.Key, out JsonNode? existing)
                    && existing is JsonObject
                        ? DeepMerge(existing, property.Value)
                        : property.Value?.DeepClone();
            }

            return merged;
        }

        return overrideNode.DeepClone();
    }

    private bool EmbeddedExists(string relativePath) => FindResourceName(relativePath) is not null;

    private string ReadEmbedded(string relativePath)
    {
        string name = FindResourceName(relativePath)
            ?? throw new RoleRenderException($"embedded role resource not found: roles/{relativePath}");
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private T ReadEmbeddedJson<T>(string relativePath, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(ReadEmbedded(relativePath), typeInfo)
            ?? throw new RoleRenderException($"embedded resource 'roles/{relativePath}' deserialized to null");

    // MSBuild's default embedded-resource naming mangles '-' to '_' in directory segments only, not
    // in the file name (2026-09-13: "code-reviewer/role.json" embeds as "...code_reviewer.role.json",
    // but "_shared/report/code-reviewer.md" keeps its hyphen). Mirror that per-segment.
    private string? FindResourceName(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
            segments[i] = segments[i].Replace('-', '_');

        string suffix = "." + string.Join('.', segments);
        return resourceNames.SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
    }
}
