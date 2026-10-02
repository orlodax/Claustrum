using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// The `tools` / `disallowedTools` pair ClaudeSync writes into each agent's frontmatter (#43, PR #42
// review): the edit rungs add role.json's `tools`, honour `withoutTools`, and disallow `Agent` for a
// role that delegates to no one — listing tools does not take a tool away, only `disallowedTools` does
// (NOTES.md, issue #19). Goldens pin the shipped files byte-for-byte; these pin the rule, so a role
// added or a local override changed later is judged by it rather than by a stale snapshot.
public sealed class ClaudeSyncToolsTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-tools-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-tools-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    [Fact]
    public void TheDemoAuthorKeepsEditAndWriteLosesNotebookEditAndHasNoAgent()
    {
        string file = SyncAndRead("demo-author", "demo-author");

        string[] tools = ToolList(file, "tools");
        Assert.Contains("Edit", tools);
        Assert.Contains("Write", tools);
        Assert.Contains("Bash", tools);
        Assert.Contains("mcp__Claude_Browser", tools);
        Assert.Contains("mcp__claude-in-chrome", tools);
        Assert.DoesNotContain("NotebookEdit", tools);
        Assert.DoesNotContain("Agent", tools);
        Assert.Equal(["Agent", "NotebookEdit"], ToolList(file, "disallowedTools"));
    }

    // role.json `tools` is added on top of the rung's own set: naming a tool the rung already grants
    // (`Write` was in the demo-author's own list before #43) must not list it twice.
    [Fact]
    public void AToolThatRoleJsonNamesAndTheRungAlreadyGrantsIsListedOnce()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"tools":["Write","Edit","mcp__Claude_Browser"]}""");

        string[] tools = ToolList(SyncAndRead("demo-author", "demo-author"), "tools");

        Assert.Equal(tools.Length, tools.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("mcp__Claude_Browser", tools);
        Assert.DoesNotContain("mcp__claude-in-chrome", tools);
    }

    [Fact]
    public void NoShippedRoleIsSyncedWithAToolListedTwice()
    {
        foreach (string role in library.ListRoles())
        {
            string[] tools = ToolList(SyncAndRead(role, role), "tools");

            Assert.Equal(tools.Length, tools.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [InlineData("tester")]
    [InlineData("tester-xhigh")]
    [InlineData("tester-max")]
    public void TheTesterAndItsTierStubsDisallowAgentAndNothingElse(string agent)
    {
        string file = SyncAndRead("tester", agent);

        Assert.Equal(["Agent"], ToolList(file, "disallowedTools"));
        string[] tools = ToolList(file, "tools");
        Assert.DoesNotContain("Agent", tools);
        Assert.Contains("Edit", tools);
        Assert.Contains("Write", tools);
        Assert.Contains("NotebookEdit", tools);
    }

    [Theory]
    [InlineData("builder", "builder")]
    [InlineData("builder", "builder-xhigh")]
    [InlineData("builder", "builder-max")]
    [InlineData("architect", "architect")]
    [InlineData("architect", "architect-xhigh")]
    [InlineData("architect", "architect-max")]
    public void ARoleThatDelegatesKeepsAgentAndCarriesNoDisallowedTools(string role, string agent)
    {
        string file = SyncAndRead(role, agent);

        Assert.Contains("Agent", ToolList(file, "tools"));
        Assert.False(HasFrontmatterKey(file, "disallowedTools"));
    }

    // Driven off ListRoles so a role added later is judged by the rule: `mayDelegate` empty means
    // Agent is withheld, non-empty means it is granted — never both, never neither.
    [Fact]
    public void AgentIsGrantedExactlyToTheRolesThatMayDelegate()
    {
        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;
            string file = SyncAndRead(role, role);

            bool granted = ToolList(file, "tools").Contains("Agent", StringComparer.Ordinal);
            bool denied = HasFrontmatterKey(file, "disallowedTools") && ToolList(file, "disallowedTools").Contains("Agent", StringComparer.Ordinal);

            Assert.Equal(definition.MayDelegate.Length > 0, granted);
            Assert.Equal(definition.MayDelegate.Length == 0, denied);
        }
    }

    // The two rungs below `edit` were not touched by the batch: pinned as the literal strings so a
    // change to the shared helper that moved them shows here and not only as a golden diff.
    [Theory]
    [InlineData("ui-reviewer")]
    [InlineData("ui-reviewer-xhigh")]
    [InlineData("ui-reviewer-max")]
    public void TheUiReviewerIsStillSyncedAtTheShellRung(string agent)
    {
        string file = SyncAndRead("ui-reviewer", agent);

        Assert.Equal(
            ["Read", "Grep", "Glob", "Bash", "PowerShell", "WebFetch", "WebSearch", "mcp__Claude_Browser", "mcp__claude-in-chrome"],
            ToolList(file, "tools"));
        Assert.Equal(["Agent", "Edit", "Write", "NotebookEdit"], ToolList(file, "disallowedTools"));
    }

    [Fact]
    public void TheCodeReviewerIsStillSyncedAtTheReadonlyRung()
    {
        string file = SyncAndRead("code-reviewer", "code-reviewer");

        Assert.Equal(["Agent", "Edit", "Write"], ToolList(file, "disallowedTools"));
        Assert.DoesNotContain("Edit", ToolList(file, "tools"));
    }

    // role.json `deny` is not rendered into `disallowedTools` — in frontmatter `Bash(git push *)`
    // removes the whole Bash tool (code.claude.com/docs/en/sub-agents, read 2026-10-02) — so no
    // Bash(...) rule may appear anywhere a sync writes: agents, tier stubs, skill, MCP config.
    [Fact]
    public void NoSyncedFileContainsABashPermissionRule()
    {
        new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd);

        string[] files = [.. Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories)];
        Assert.NotEmpty(files);
        foreach (string file in files)
            Assert.DoesNotContain("Bash(", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalEmptyWithoutToolsRestoresNotebookEditForTheDemoAuthor()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"withoutTools":[]}""");

        string file = SyncAndRead("demo-author", "demo-author");

        Assert.Contains("NotebookEdit", ToolList(file, "tools"));
        Assert.Equal(["Agent"], ToolList(file, "disallowedTools"));
    }

    // A role that delegates keeps `Agent` in its tools, so `withoutTools` alone fills the disallowed
    // list: the other branch of the same expression.
    [Fact]
    public void ALocalWithoutToolsOnADelegatingRoleDropsThemAndKeepsAgent()
    {
        WriteLocalRoleJson("builder", /*lang=json,strict*/ """{"withoutTools":["NotebookEdit","Write"]}""");

        string file = SyncAndRead("builder", "builder");

        string[] tools = ToolList(file, "tools");
        Assert.DoesNotContain("NotebookEdit", tools);
        Assert.DoesNotContain("Write", tools);
        Assert.Contains("Edit", tools);
        Assert.Contains("Agent", tools);
        Assert.Equal(["NotebookEdit", "Write"], ToolList(file, "disallowedTools"));
    }

    // The tier stubs share the base agent's tool lines, so a local override reaches them too.
    [Fact]
    public void ALocalWithoutToolsReachesTheTierStubsToo()
    {
        WriteLocalRoleJson("tester", /*lang=json,strict*/ """{"withoutTools":["NotebookEdit"]}""");
        NewSync().Sync(cwd, roles: ["tester"]);

        foreach (string agent in new[] { "tester", "tester-xhigh", "tester-max" })
        {
            string file = File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{agent}.md"));

            Assert.DoesNotContain("NotebookEdit", ToolList(file, "tools"));
            Assert.Equal(["Agent", "NotebookEdit"], ToolList(file, "disallowedTools"));
        }
    }

    [Fact]
    public void ALocalMayDelegateTurnsTheTesterIntoADelegatorThatKeepsAgent()
    {
        WriteLocalRoleJson("tester", /*lang=json,strict*/ """{"mayDelegate":["builder"]}""");

        string file = SyncAndRead("tester", "tester");

        Assert.Contains("Agent", ToolList(file, "tools"));
        Assert.False(HasFrontmatterKey(file, "disallowedTools"));
    }

    private ClaudeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    private string SyncAndRead(string role, string agent)
    {
        NewSync().Sync(cwd, roles: [role]);
        return File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{agent}.md"));
    }

    private void WriteLocalRoleJson(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    // The frontmatter is the block between the first two `---` lines; looking only there keeps a
    // `tools:` line in the body prose from ever answering for the key.
    private static string[] ToolList(string file, string key)
    {
        string prefix = $"{key}: ";
        string? line = Frontmatter(file).FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool HasFrontmatterKey(string file, string key) =>
        Frontmatter(file).Any(line => line.StartsWith($"{key}: ", StringComparison.Ordinal));

    private static string[] Frontmatter(string file) =>
        [.. file.Split('\n').Skip(1).TakeWhile(line => line != "---")];
}
