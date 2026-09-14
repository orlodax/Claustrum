using System.Text.Json;
using Claustrum.Mcp;

namespace Claustrum.Tests.Mcp;

// ClaustrumTools methods are plain static C# methods (the [McpServerTool]/[McpServerToolType]
// attributes only matter to the MCP host, verified separately by publishing+probing the real server —
// see NOTES.md), so they are testable directly without a transport.
public sealed class ClaustrumToolsTests
{
    [Fact]
    public void ListRolesReturnsValidJsonNamingEveryLibraryRole()
    {
        string json = ClaustrumTools.ListRoles();

        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

        Assert.Contains("builder", names);
        Assert.Contains("code-reviewer", names);
        Assert.Contains("tester", names);
    }

    [Fact]
    public void ListRolesMarksCodeReviewerAsBlind()
    {
        string json = ClaustrumTools.ListRoles();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement codeReviewer = document.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "code-reviewer");

        Assert.True(codeReviewer.GetProperty("blind").GetBoolean());
    }
}
