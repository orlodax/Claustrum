using Claustrum.Core.Config;

namespace Claustrum.Roles.Tests;

// role.json `permissionByBackend` replaces `permission` on the named backends: today only the architect, `full` on codex, whose
// workspace-write sandbox cannot write `~/.claustrum` or a linked worktree's git dir (NOTES.md "The codex backend, validated…").
public sealed class RolePermissionByBackendTests
{
    private readonly RoleLibrary library = new();

    [Fact]
    public void EveryEntryNamesAHarnessTheRoleListsAndALevelThatParses()
    {
        foreach (string role in library.ListRoles())
        {
            Dictionary<string, string> byBackend = library.LoadRole(role, cwd: null).Definition.PermissionByBackend ?? [];
            string[] harnesses = library.LoadRole(role, cwd: null).Definition.Harnesses;
            foreach ((string backend, string level) in byBackend)
            {
                Assert.Contains(backend, harnesses);
                Assert.True(PermissionLevelParser.TryParse(level, out _), $"role '{role}': '{level}' on {backend} is not a permission level");
            }
        }
    }

    [Fact]
    public void OnlyTheArchitectChangesLevelAndOnlyOnCodex()
    {
        RoleRenderer renderer = new(library);

        Assert.Equal(new Dictionary<string, string> { ["codex"] = "full" }, renderer.Render("architect", "high", "codex", cwd: null).PermissionByBackend);
        foreach (string role in library.ListRoles().Where(role => role != "architect"))
            Assert.Null(library.LoadRole(role, cwd: null).Definition.PermissionByBackend);
    }
}
