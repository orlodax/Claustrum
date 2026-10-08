using System.ComponentModel;
using System.Reflection;
using Claustrum.Mcp;
using ModelContextProtocol.Server;

namespace Claustrum.Tests.Mcp;

// docs/PLAN.md §A6/§D5 fixes the MCP surface at eleven tools (M4 added `coordinate`). Nothing else
// asserted the surface itself, so a tool silently renamed, dropped, or added (the generated
// SKILL.md names six of them by hand — see GeneratedSkillMatchesCliTests) was invisible to the suite.
public sealed class McpToolSurfaceTests
{
    private static readonly string[] expectedTools =
    [
        "cast_create", "cast_list", "cast_questions", "coordinate", "delegate", "delegate_async",
        "doctor", "job_result", "job_status", "list_backends", "list_roles",
    ];

    [Fact]
    public void ClaustrumToolsExposesExactlyTheElevenPlannedTools()
    {
        Assert.Equal(expectedTools, Tools().Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryToolCarriesADescriptionForTheHostToShow()
    {
        foreach ((string name, MethodInfo method) in Tools())
            Assert.False(string.IsNullOrWhiteSpace(method.GetCustomAttribute<DescriptionAttribute>()?.Description), $"tool '{name}' has no [Description]");
    }

    // Finding #6 was job_result's description contradicting its behaviour, and the fix added a
    // 'failed' state to job_status. A description that omits a state the tool can return is the same
    // defect again, so pin every state JobManager.GetStatus (plus ClaustrumTools' 'unknown' fallback)
    // can produce — JobManagerTests asserts the other direction, that no other state is produced.
    [Theory]
    [InlineData("running")]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public void JobStatusDescriptionNamesEveryStateItCanReturn(string state)
    {
        MethodInfo jobStatus = Tools().Single(tool => tool.Name == "job_status").Method;

        Assert.Contains(state, jobStatus.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }

    // #63: `branch` on delegate and delegate_async, the same words as `run --branch`, plus what comes back.
    [Theory]
    [InlineData("delegate")]
    [InlineData("delegate_async")]
    public void DelegateAndDelegateAsyncTakeAnOptionalBranchParameterThatSaysWhatItDoes(string tool)
    {
        MethodInfo method = Tools().Single(entry => entry.Name == tool).Method;

        ParameterInfo branch = Assert.Single(method.GetParameters(), parameter => parameter.Name == "branch");
        Assert.Equal(typeof(string), branch.ParameterType);
        Assert.True(branch.IsOptional);
        string description = branch.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        Assert.Contains("Run isolated in a new worktree checked out on this existing branch", description, StringComparison.Ordinal);
        Assert.Contains("The result's worktree and branch name it, and commit is the branch tip the run left.", description, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Name, MethodInfo Method)> Tools() =>
        typeof(ClaustrumTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (Attribute: method.GetCustomAttribute<McpServerToolAttribute>(), Method: method))
            .Where(entry => entry.Attribute is not null)
            .Select(entry => (entry.Attribute!.Name!, entry.Method));
}
