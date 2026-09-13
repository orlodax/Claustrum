namespace Claustrum.Roles.Tests;

// RoleRenderException per docs/PLAN.md §B2 "Unknown token = render error" and NOTES.md "Role
// templating recurses one level into {{part:name}}" (the MaxPartDepth backstop).
public sealed class RoleRendererErrorTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-render-error-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public void UnknownTokenInALocalOverrideThrows()
    {
        WriteLocalRoleMd("builder", "Body with an {{unknown_token}} in it.");
        RoleRenderer renderer = new(new RoleLibrary());

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => renderer.Render("builder", "high", "claude", cwd));
        Assert.Contains("unknown_token", ex.Message);
    }

    [Fact]
    public void UnknownTierThrows()
    {
        RoleRenderer renderer = new(new RoleLibrary());

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => renderer.Render("builder", "does-not-exist", "claude", cwd));
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void PartSelfRecursionHitsTheMaxDepthBackstopInsteadOfAStackOverflow()
    {
        // Overrides the claude-specific delegation part to reference itself, so resolving
        // {{part:delegation}} recurses forever unless RoleRenderer.MaxPartDepth stops it.
        string partsDir = Path.Combine(cwd, ".claustrum", "roles", "builder", "parts");
        Directory.CreateDirectory(partsDir);
        File.WriteAllText(Path.Combine(partsDir, "delegation.claude.md"), "{{part:delegation}}");
        RoleRenderer renderer = new(new RoleLibrary());

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => renderer.Render("builder", "high", "claude", cwd));
        Assert.Contains("nesting exceeded", ex.Message);
    }

    private void WriteLocalRoleMd(string role, string body)
    {
        string roleDir = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(roleDir);
        File.WriteAllText(Path.Combine(roleDir, "ROLE.md"), body);
    }
}
