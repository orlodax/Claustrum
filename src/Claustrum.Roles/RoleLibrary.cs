using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Claustrum.Roles.Json;
using Claustrum.Roles.Model;

namespace Claustrum.Roles;

/// <summary>
/// Loads the embedded `roles/` library (docs/PLAN.md §B2), with `&lt;cwd&gt;/.claustrum/roles/&lt;role&gt;/`
/// winning over it file-by-file. `role.json` is deep-merged rather than replaced wholesale.
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

    public LoadedRole LoadRole(string role, string cwd)
    {
        string embeddedJson = ReadEmbedded($"{role}/role.json");
        JsonNode baseNode = JsonNode.Parse(embeddedJson)
            ?? throw new RoleRenderException($"role '{role}': embedded role.json parsed to null");

        string localDir = Path.Combine(cwd, ".claustrum", "roles", role);
        string localRoleJsonPath = Path.Combine(localDir, "role.json");
        string localRoleMdPath = Path.Combine(localDir, "ROLE.md");
        bool isLocalOverride = File.Exists(localRoleJsonPath) || File.Exists(localRoleMdPath);

        JsonNode mergedNode = baseNode;
        if (File.Exists(localRoleJsonPath))
        {
            JsonNode overrideNode = JsonNode.Parse(File.ReadAllText(localRoleJsonPath))
                ?? throw new RoleRenderException($"role '{role}': local role.json parsed to null");
            mergedNode = DeepMerge(baseNode, overrideNode);
        }

        RoleDefinition definition = JsonSerializer.Deserialize(mergedNode.ToJsonString(), RolesJsonContext.Default.RoleDefinition)
            ?? throw new RoleRenderException($"role '{role}': merged role.json deserialized to null");

        string roleMd = File.Exists(localRoleMdPath)
            ? File.ReadAllText(localRoleMdPath)
            : ReadEmbedded($"{role}/ROLE.md");

        return new LoadedRole(definition, roleMd, isLocalOverride);
    }

    /// <summary>Reads a `_shared/...` file (house rules, report format, tier stub template).</summary>
    public string ReadShared(string relativePath) => ReadEmbedded(relativePath);

    /// <summary>
    /// Resolves `{{part:name}}`: local override wins over embedded, harness-specific file wins over
    /// `.default.md` (docs/PLAN.md §B2).
    /// </summary>
    public string ReadPart(string role, string partName, string harness, string cwd)
    {
        string harnessRel = $"{role}/parts/{partName}.{harness}.md";
        string defaultRel = $"{role}/parts/{partName}.default.md";
        string localRoot = Path.Combine(cwd, ".claustrum", "roles");

        foreach (string rel in new[] { harnessRel, defaultRel })
        {
            string localPath = Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
                return File.ReadAllText(localPath);
        }

        foreach (string rel in new[] { harnessRel, defaultRel })
        {
            if (EmbeddedExists(rel))
                return ReadEmbedded(rel);
        }

        throw new RoleRenderException($"role '{role}': no part '{partName}' for harness '{harness}' (looked for {harnessRel} and {defaultRel})");
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
