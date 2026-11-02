using System.ComponentModel;
using System.Reflection;
using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Mcp;
using Claustrum.Roles;
using Claustrum.Roles.Sync;

namespace Claustrum.Tests.Cli;

// #145: the texts that tell an agent what to do with a result that is not clean. An agent that never reads this rule cannot honour the exit code, so the
// three places an agent reads it - the generated /claustrum skill, a spawned architect's coordination brief, and the MCP tool descriptions - say it.
public sealed class EnvironmentIssuesGuidanceTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-guidance-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-guidance-home-").FullName;

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private static string Description(string methodName) =>
        typeof(ClaustrumTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void TheGeneratedSkillTellsAnAgentToStopAndReportWhatTheJobCouldNotReach()
    {
        RoleLibrary library = new();
        new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: ["builder"]);

        string skill = File.ReadAllText(Path.Combine(cwd, ".claude", "skills", "claustrum", "SKILL.md"));

        Assert.Contains("`success_with_issues`", skill, StringComparison.Ordinal);
        Assert.Contains("`environment_issues`", skill, StringComparison.Ordinal);
        Assert.Contains("warning: environment issues", skill, StringComparison.Ordinal);
        Assert.Contains("do not use its verdict", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnedArchitectsBriefTellsItWhatToDoWithAChildThatIsNotCleanAndWhereToPutWhatItCannotFix()
    {
        Cast cast = new("default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"), [], null);

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("neither `success` nor `success_with_issues`", appendix, StringComparison.Ordinal);
        Assert.Contains("A child whose `status` is `success_with_issues`, or whose `environment_issues` is not empty", appendix, StringComparison.Ordinal);
        Assert.Contains("goes in your report's `blocked_by`", appendix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DelegateAsync", "success_with_issues")]
    [InlineData("DelegateAsync", "environment_issues")]
    [InlineData("JobStatus", "success_with_issues")]
    [InlineData("JobStatus", "environment_issues")]
    public void TheMcpToolsThatReturnAReceiptDescribeTheNewStatusAndTheIssues(string method, string phrase)
    {
        Assert.Contains(phrase, Description(method), StringComparison.Ordinal);
    }
}
