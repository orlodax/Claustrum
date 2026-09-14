using System.ComponentModel;
using System.Text.Json;
using Claustrum.Mcp.Json;
using Claustrum.Roles.Model;
using ModelContextProtocol.Server;

namespace Claustrum.Mcp;

// docs/PLAN.md §A6: the 10 MCP tools, thin adapters over the same DelegateEngine/JobManager/
// RoleLibrary/BackendRegistry/CastStore the CLI uses — no logic lives here that the CLI does not
// already exercise. Every tool returns a pre-serialized JSON string.
[McpServerToolType]
public sealed class ClaustrumTools
{
    [McpServerTool(Name = "list_roles")]
    [Description("List every role in the embedded role library, with its description, blind flag, and supported harnesses.")]
    public static string ListRoles()
    {
        string cwd = Environment.CurrentDirectory;
        RoleSummary[] summaries = [.. AppServices.RoleLibrary.ListRoles().Select(role =>
        {
            RoleDefinition definition = AppServices.RoleLibrary.LoadRole(role, cwd).Definition;
            return new RoleSummary(definition.Name, definition.Description, definition.Blind, definition.Harnesses);
        })];

        return JsonSerializer.Serialize(summaries, McpJsonContext.Default.RoleSummaryArray);
    }
}
