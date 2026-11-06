using Claustrum.Roles.Model;

namespace Claustrum.Roles.Tests;

// `withoutTools` (PR #42 review) is optional like `tools`: a role.json that names neither behaves
// exactly as before, and the demo-author is the one shipped role that drops a tool its permission grants.
public sealed class RoleDefinitionTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-definition-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Theory]
    [InlineData("architect")]
    [InlineData("builder")]
    [InlineData("code-reviewer")]
    [InlineData("tester")]
    [InlineData("ui-reviewer")]
    public void ARoleWithNoWithoutToolsKeyDropsNothing(string role)
    {
        RoleDefinition definition = library.LoadRole(role, cwd).Definition;

        Assert.Null(definition.WithoutTools);
        Assert.Empty(definition.DroppedTools);
    }

    [Fact]
    public void TheDemoAuthorDropsExactlyNotebookEdit()
    {
        RoleDefinition definition = library.LoadRole("demo-author", cwd).Definition;

        Assert.NotNull(definition.WithoutTools);
        Assert.Equal(["NotebookEdit"], definition.WithoutTools);
        Assert.Equal(["NotebookEdit"], definition.DroppedTools);
    }

    [Fact]
    public void ARoleWithNoToolsKeyHasNoExtraTools()
    {
        RoleDefinition definition = library.LoadRole("builder", cwd).Definition;

        Assert.Null(definition.Tools);
        Assert.Empty(definition.ExtraTools);
    }

    // Arrays replace wholesale in the local-override merge (RoleLibrary.DeepMerge), so an empty one
    // is how a repo takes the drop back.
    [Fact]
    public void ALocalOverrideWithAnEmptyArrayEmptiesDroppedTools()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"withoutTools":[]}""");

        RoleDefinition definition = library.LoadRole("demo-author", cwd).Definition;

        Assert.Empty(definition.DroppedTools);
    }

    [Fact]
    public void ALocalOverrideCanAddAWithoutToolsKeyToARoleThatHadNone()
    {
        WriteLocalRoleJson("builder", /*lang=json,strict*/ """{"withoutTools":["NotebookEdit","WebFetch"]}""");

        RoleDefinition definition = library.LoadRole("builder", cwd).Definition;

        Assert.Equal(["NotebookEdit", "WebFetch"], definition.DroppedTools);
    }

    // The harness refusal (DelegateEngine.RequireSupportedHarness) and the questionnaire's alias
    // filter both read this list, so the two browser roles running on claude and opencode is a
    // contract, not a detail (#135).
    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public void TheBrowserRolesRunOnClaudeAndOpencode(string role) =>
        Assert.Equal(["claude", "opencode"], library.LoadRole(role, cwd).Definition.Harnesses);

    // #135: GetExtraTools(harness) is role.json's per-harness override when it names one, else the flat
    // `tools` — a browser role's Playwright verbs on opencode, its two claude browser servers on claude.
    [Fact]
    public void TheBrowserRoleNamesPlaywrightOnOpencodeAndItsClaudeServersOnClaude()
    {
        RoleDefinition definition = library.LoadRole("ui-reviewer", cwd).Definition;

        Assert.Equal(["playwright_*"], definition.GetExtraTools("opencode"));
        Assert.Equal(["mcp__Claude_Browser", "mcp__claude-in-chrome"], definition.GetExtraTools("claude"));
    }

    // #135: the demo-author's opencode override is explicitly empty (a scripted driver, no MCP verbs): an
    // absent override would return the flat `tools` and leak the two Claude-only servers onto opencode.
    [Fact]
    public void TheDemoAuthorNamesNoExtraToolsOnOpencodeAndItsClaudeServersOnClaude()
    {
        RoleDefinition definition = library.LoadRole("demo-author", cwd).Definition;

        Assert.Empty(definition.GetExtraTools("opencode"));
        Assert.Equal(["mcp__Claude_Browser", "mcp__claude-in-chrome"], definition.GetExtraTools("claude"));
    }

    // A role naming no override for a harness falls back to its flat `tools`; a role with neither is empty.
    [Theory]
    [InlineData("claude")]
    [InlineData("cursor")]
    public void ARoleWithoutToolsByHarnessFallsBackToItsExtraToolsForAnyHarness(string harness)
    {
        RoleDefinition demoAuthor = library.LoadRole("demo-author", cwd).Definition;
        Assert.Equal(["mcp__Claude_Browser", "mcp__claude-in-chrome"], demoAuthor.GetExtraTools(harness));

        RoleDefinition builder = library.LoadRole("builder", cwd).Definition;
        Assert.Empty(builder.GetExtraTools(harness));
    }

    private void WriteLocalRoleJson(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }
}
