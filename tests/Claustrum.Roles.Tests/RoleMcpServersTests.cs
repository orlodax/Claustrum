using Claustrum.Core.Model;

namespace Claustrum.Roles.Tests;

// #140: the MCP servers a `shell`-level claude run may call unasked start from the role library's own role.json `tools` (`mcp__<server>` or
// `mcp__<server>__<tool>`). A repo's `.claustrum/roles/<role>/role.json` override is merged for everything else but must not add one: a cloned repo
// naming `mcp__<personal server>` there would have the run call that server without a prompt. `roles.<role>.mcp_servers` (user config) is the way in.
public sealed class RoleMcpServersTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-mcpservers-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private void WriteOverride(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    [Fact]
    public void TheUiReviewerNamesItsTwoBrowserServersAndTheOtherRolesNone()
    {
        RoleRenderer renderer = new(new RoleLibrary());

        Assert.Equal(["Claude_Browser", "claude-in-chrome"], renderer.Render("ui-reviewer", "high", "claude", cwd: null).McpServers!);
        foreach (string role in new[] { "builder", "code-reviewer", "tester", "architect" })
            Assert.Empty(renderer.Render(role, "high", "claude", cwd: null).McpServers!);
    }

    // #135: on opencode the ui-reviewer's server is named by `mcpServersByHarness` (opencode's
    // `playwright` has no `mcp__` prefix to parse out of `tools`), and it wins over `LibraryMcpServers`.
    [Fact]
    public void TheUiReviewerNamesPlaywrightOnOpencodeAndItsClaudeServersOnClaude()
    {
        RoleRenderer renderer = new(new RoleLibrary());

        Assert.Equal(["playwright"], renderer.Render("ui-reviewer", "high", "opencode", cwd: null).McpServers!);
        Assert.Equal(["Claude_Browser", "claude-in-chrome"], renderer.Render("ui-reviewer", "high", "claude", cwd: null).McpServers!);
    }

    // #135: the demo-author drives the browser with a scripted Playwright driver on opencode, not an MCP
    // server, so its opencode override is explicitly empty — an absent override would fall back to the flat
    // `tools` and leak the two Claude-only servers. Claude still names them.
    [Fact]
    public void TheDemoAuthorNamesNoServerOnOpencodeAndItsClaudeServersOnClaude()
    {
        RoleRenderer renderer = new(new RoleLibrary());

        Assert.Empty(renderer.Render("demo-author", "high", "opencode", cwd: null).McpServers!);
        Assert.Equal(["Claude_Browser", "claude-in-chrome"], renderer.Render("demo-author", "high", "claude", cwd: null).McpServers!);
    }

    [Fact]
    public void ARepoOverrideThatNamesAnotherServerAddsNoneToTheApprovedOnes()
    {
        WriteOverride("ui-reviewer", /*lang=json,strict*/ """{"tools":["mcp__my_personal_server__post","mcp__other"]}""");

        RenderedRole rendered = new RoleRenderer(new RoleLibrary()).Render("ui-reviewer", "high", "claude", cwd);

        Assert.Equal(["Claude_Browser", "claude-in-chrome"], rendered.McpServers!);
    }

    [Fact]
    public void ARepoOverrideOfARoleWithNoServersLeavesItWithNone()
    {
        WriteOverride("builder", /*lang=json,strict*/ """{"tools":["mcp__my_personal_server"]}""");

        RenderedRole rendered = new RoleRenderer(new RoleLibrary()).Render("builder", "high", "claude", cwd);

        Assert.Empty(rendered.McpServers!);
    }
}
