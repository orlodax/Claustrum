namespace Claustrum.Roles.Tests;

// #127 finding 7/8: `userMcp` in role.json is the role library's default for keeping the operator's personal MCP servers. A repo's own
// `.claustrum/roles/<role>/role.json` cannot set it (it is dropped with a warning that reaches the render), and the two browser roles default to it.
public sealed class RoleUserMcpTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-usermcp-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private void WriteOverride(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    [Theory]
    [InlineData("ui-reviewer", true)]
    [InlineData("demo-author", true)]
    [InlineData("builder", false)]
    [InlineData("code-reviewer", false)]
    [InlineData("tester", false)]
    public void OnlyTheBrowserRolesDefaultToUserMcp(string role, bool expected)
    {
        RoleRenderer renderer = new(new RoleLibrary());

        Assert.Equal(expected, renderer.Render(role, "high", "claude", cwd: null).UserMcp);
    }

    [Fact]
    public void ARepoOverrideCannotTurnUserMcpOnAndTheWarningReachesTheRender()
    {
        WriteOverride("builder", /*lang=json,strict*/ """{"userMcp":true,"description":"still merged"}""");

        RoleLibrary library = new();
        LoadedRole loaded = library.LoadRole("builder", cwd);

        Assert.False(loaded.Definition.UserMcp);
        Assert.Equal("still merged", loaded.Definition.Description);
        string warning = Assert.Single(loaded.Warnings!);
        Assert.Contains("userMcp", warning);
        Assert.Contains(Path.Combine(cwd, ".claustrum", "roles", "builder", "role.json"), warning);

        Assert.Contains(warning, new RoleRenderer(library).Render("builder", "high", "claude", cwd).Warnings!);
    }

    [Fact]
    public void ARepoOverrideCannotTurnUserMcpOffEitherSoTheLibraryDefaultHolds()
    {
        WriteOverride("ui-reviewer", /*lang=json,strict*/ """{"userMcp":false}""");

        LoadedRole loaded = new RoleLibrary().LoadRole("ui-reviewer", cwd);

        Assert.True(loaded.Definition.UserMcp);
        Assert.NotNull(loaded.Warnings);
    }

    [Fact]
    public void AnOverrideThatSaysNothingAboutItProducesNoWarning()
    {
        WriteOverride("builder", /*lang=json,strict*/ """{"description":"only this"}""");

        LoadedRole loaded = new RoleLibrary().LoadRole("builder", cwd);

        Assert.Null(loaded.Warnings);
    }
}
