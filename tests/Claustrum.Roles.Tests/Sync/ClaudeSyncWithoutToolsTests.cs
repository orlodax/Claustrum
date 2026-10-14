using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #45 beyond the edit rung (ClaudeSyncToolsTests): `withoutTools` is honoured at the readonly and shell rungs too, where a
// dropped tool leaves `tools` and joins `disallowedTools`; a tool role.json both grants and withholds is denied only; one
// role.json grants on a role that delegates to no one is granted and not denied; and no shipped golden moves.
public sealed class ClaudeSyncWithoutToolsTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-without-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-without-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(fakeHome);
    }

    private string SyncAndRead(string role)
    {
        new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: [role]);
        return File.ReadAllText(Path.Combine(cwd, ".claude", "agents", $"{role}.md"));
    }

    private void WriteLocalRoleJson(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    private static string[] ToolList(string file, string key)
    {
        string prefix = $"{key}: ";
        string? line = file.Split('\n').Skip(1).TakeWhile(l => l != "---").FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void WithoutToolsBashOnTheShellRungLeavesToolsAndJoinsDisallowedTools()
    {
        WriteLocalRoleJson("ui-reviewer", /*lang=json,strict*/ """{"withoutTools":["Bash"]}""");

        string file = SyncAndRead("ui-reviewer");

        Assert.DoesNotContain("Bash", ToolList(file, "tools"));
        Assert.Equal(["Agent", "Edit", "Write", "NotebookEdit", "Bash"], ToolList(file, "disallowedTools"));
    }

    [Fact]
    public void WithoutToolsBashOnTheReadonlyRungLeavesToolsAndJoinsDisallowedTools()
    {
        WriteLocalRoleJson("code-reviewer", /*lang=json,strict*/ """{"withoutTools":["Bash"]}""");

        string file = SyncAndRead("code-reviewer");

        string[] tools = ToolList(file, "tools");
        Assert.DoesNotContain("Bash", tools);
        Assert.Contains("Read", tools);
        Assert.Equal(["Agent", "Edit", "Write", "Bash"], ToolList(file, "disallowedTools"));
    }

    [Fact]
    public void AToolBothGrantedAndWithheldIsDeniedOnly()
    {
        WriteLocalRoleJson("tester", /*lang=json,strict*/ """{"tools":["Agent"],"withoutTools":["Agent"]}""");

        string file = SyncAndRead("tester");

        Assert.DoesNotContain("Agent", ToolList(file, "tools"));
        Assert.Equal(["Agent"], ToolList(file, "disallowedTools"));
    }

    [Fact]
    public void ToolsAgentOnAnEditShellRoleThatDelegatesToNoOneIsGrantedAndNotDenied()
    {
        WriteLocalRoleJson("tester", /*lang=json,strict*/ """{"tools":["Agent"]}""");

        string file = SyncAndRead("tester");

        Assert.Contains("Agent", ToolList(file, "tools"));
        Assert.DoesNotContain(file.Split('\n').Skip(1).TakeWhile(l => l != "---"), l => l.StartsWith("disallowedTools:", StringComparison.Ordinal));
    }

    [Fact]
    public void ByDefaultAnEditShellRoleThatDelegatesToNoOneHasAgentDenied()
    {
        string file = SyncAndRead("tester");

        Assert.DoesNotContain("Agent", ToolList(file, "tools"));
        Assert.Equal(["Agent"], ToolList(file, "disallowedTools"));
    }
}
