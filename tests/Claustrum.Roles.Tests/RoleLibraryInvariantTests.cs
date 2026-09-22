using Claustrum.Core.Model;
using Claustrum.Roles.Model;

namespace Claustrum.Roles.Tests;

// Whole-library invariants, one test each, driven by RoleLibrary.ListRoles() rather than a
// hardcoded role name list — so a role added later (M4's "architect" is the first case) is covered
// automatically instead of silently skipped the way a fixed InlineData set would have left it.
public sealed class RoleLibraryInvariantTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-invariants-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    // RoleRenderer.ComposeSystemBody is the only place these two headings belong (docs/PLAN.md §B2):
    // a role's own ROLE.md hand-authoring one would either collide with the renderer's own section or
    // — since ComposeSystemBody splits on the first `## ` heading — get folded into "the opening
    // description" and silently misplaced.
    [Fact]
    public void NoRoleMdHandAuthorsTheReportFormatOrHouseRulesHeadings()
    {
        foreach (string role in library.ListRoles())
        {
            string roleMd = library.LoadRole(role, cwd).RoleMdTemplate;
            Assert.DoesNotContain("## Report format", roleMd, StringComparison.Ordinal);
            Assert.DoesNotContain("## House rules", roleMd, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryRolesReportSchemaHasASharedReportFile()
    {
        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;

            // Throws RoleRenderException (embedded resource not found) rather than returning null/
            // empty, so a missing `_shared/report/<name>.md` fails loudly here instead of at the
            // first real render of that role.
            string reportFormat = library.ReadShared($"_shared/report/{definition.Report}.md");
            Assert.False(string.IsNullOrWhiteSpace(reportFormat), $"role '{role}': report schema '{definition.Report}' is empty");
        }
    }

    [Fact]
    public void EveryRoleRendersForEveryHarnessItLists()
    {
        RoleRenderer renderer = new(library);

        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;
            foreach (string harness in definition.Harnesses)
            {
                RenderedRole rendered = renderer.Render(role, "high", harness, cwd);
                Assert.False(string.IsNullOrWhiteSpace(rendered.SystemBody), $"role '{role}' harness '{harness}': empty system body");
            }
        }
    }

    [Fact]
    public void EveryRolesXhighAndMaxTierStubRenders()
    {
        RoleRenderer renderer = new(library);

        foreach (string role in library.ListRoles())
        {
            RoleDefinition definition = library.LoadRole(role, cwd).Definition;
            foreach (string tier in new[] { "xhigh", "max" })
            {
                if (!definition.Tiers.ContainsKey(tier))
                    continue;

                string stub = renderer.RenderTierStub(role, tier, cwd);
                Assert.Contains($"effort `{definition.Tiers[tier].Effort}`", stub, StringComparison.Ordinal);
                Assert.Contains("## Non-negotiable", stub, StringComparison.Ordinal);
            }
        }
    }
}
