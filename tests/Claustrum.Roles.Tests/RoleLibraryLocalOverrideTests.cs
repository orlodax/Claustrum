namespace Claustrum.Roles.Tests;

// docs/PLAN.md §B4 "Local override ... role.json deep-merged"; RoleLibrary.DeepMerge recurses into
// nested JSON objects but replaces arrays wholesale (a JsonArray is never a JsonObject).
public sealed class RoleLibraryLocalOverrideTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-override-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public void LocalRoleJsonDeepMergesOverTheEmbeddedDefinition()
    {
        string roleDir = Path.Combine(cwd, ".claustrum", "roles", "builder");
        Directory.CreateDirectory(roleDir);
        File.WriteAllText(Path.Combine(roleDir, "role.json"), /*lang=json,strict*/ """
            {"description":"Overridden description","tiers":{"high":{"effort":"max"}},"deny":["custom-deny"]}
            """);

        RoleLibrary library = new();
        LoadedRole loaded = library.LoadRole("builder", cwd);

        Assert.True(loaded.IsLocalOverride);
        Assert.Equal("Overridden description", loaded.Definition.Description);
        Assert.Equal("frontier-coding", loaded.Definition.Tiers["high"].Model); // untouched by the merge
        Assert.Equal("max", loaded.Definition.Tiers["high"].Effort); // overridden
        Assert.Equal(["custom-deny"], loaded.Definition.Deny); // arrays replace wholesale, not concatenate
        Assert.Equal("edit+shell", loaded.Definition.Permission); // untouched
        Assert.Equal("xhigh", loaded.Definition.Tiers["xhigh"].Effort); // sibling tier untouched
    }

    [Fact]
    public void NoLocalOverrideUsesTheEmbeddedDefinitionUnchanged()
    {
        RoleLibrary library = new();
        LoadedRole loaded = library.LoadRole("builder", cwd);

        Assert.False(loaded.IsLocalOverride);
        Assert.Equal(["git push"], loaded.Definition.Deny);
    }
}
