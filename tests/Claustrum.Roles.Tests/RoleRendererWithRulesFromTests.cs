using Claustrum.Core.Model;
using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// #55: an isolated api run inlines the rules of the worktree it runs in, not the main checkout's. WithRulesFrom re-reads them
// into an already-rendered prompt and touches nothing before `## House rules`.
public sealed class RoleRendererWithRulesFromTests : IDisposable
{
    private readonly RoleLibrary library = new();
    private readonly string main = Directory.CreateTempSubdirectory("claustrum-rules-main-").FullName;
    private readonly string branch = Directory.CreateTempSubdirectory("claustrum-rules-branch-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-rules-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(main);
        LinkFixtures.DeleteTree(branch);
        LinkFixtures.DeleteTree(outside);
    }

    private RoleRenderer Renderer => new(library);

    private ResolvedRole Resolved(string harness, string cwd, string? appendix = null)
    {
        RenderedRole rendered = Renderer.Render("code-reviewer", "high", harness, cwd);
        string body = rendered.SystemBody;
        if (appendix is not null)
        {
            int rules = body.IndexOf("\n## House rules", StringComparison.Ordinal);
            body = $"{body[..rules]}\n\n{appendix}\n{body[rules..]}";
        }

        return new ResolvedRole(rendered.Name, body, harness, "some-model", rendered.Effort, new PermissionPolicy(PermissionLevel.ReadOnly, []), rendered.Blind, HasReport: true, rendered.Warnings);
    }

    [Fact]
    public void TheBranchRulesReplaceTheMainRules()
    {
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "MAIN\n");
        File.WriteAllText(Path.Combine(branch, "AGENTS.md"), "BRANCH\n");

        ResolvedRole rewritten = Renderer.WithRulesFrom(Resolved("api", main), branch);

        Assert.Contains("BRANCH", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MAIN", rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultEqualsARenderAgainstTheRulesDirectoryWhenThereIsNoAppendix()
    {
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "MAIN\n");
        File.WriteAllText(Path.Combine(branch, "AGENTS.md"), "BRANCH\n");
        File.WriteAllText(Path.Combine(branch, "CLAUDE.md"), "CLAUDE-BRANCH\n");

        ResolvedRole rewritten = Renderer.WithRulesFrom(Resolved("api", main), branch);
        RenderedRole direct = Renderer.Render("code-reviewer", "high", "api", branch);

        Assert.Equal(direct.SystemBody, rewritten.SystemPrompt);
    }

    [Fact]
    public void AnAppendixAheadOfTheHouseRulesIsPreserved()
    {
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "MAIN\n");
        File.WriteAllText(Path.Combine(branch, "AGENTS.md"), "BRANCH\n");

        ResolvedRole rewritten = Renderer.WithRulesFrom(Resolved("api", main, "APPENDIX-TEXT"), branch);

        Assert.Contains("APPENDIX-TEXT", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.True(rewritten.SystemPrompt.IndexOf("APPENDIX-TEXT", StringComparison.Ordinal) < rewritten.SystemPrompt.IndexOf("## House rules", StringComparison.Ordinal));
        Assert.Contains("BRANCH", rewritten.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarningsAreReplacedByTheReReadsNotAddedTo()
    {
        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, "SECRET");
        LinkFixtures.FileLink(Path.Combine(main, "AGENTS.md"), secret);
        ResolvedRole original = Resolved("api", main);
        Assert.Contains("AGENTS.md not inlined", Assert.Single(original.Warnings ?? []), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(branch, "AGENTS.md"), "BRANCH\n");

        ResolvedRole rewritten = Renderer.WithRulesFrom(original, branch);

        Assert.Null(rewritten.Warnings);
    }

    [Fact]
    public void ANewWarningFromTheBranchReplacesTheMainOnesAndTheLinkIsNotInlined()
    {
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "MAIN\n");
        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, "SECRET");
        LinkFixtures.FileLink(Path.Combine(branch, "AGENTS.md"), secret);

        ResolvedRole rewritten = Renderer.WithRulesFrom(Resolved("api", main), branch);

        Assert.DoesNotContain("SECRET", rewritten.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("AGENTS.md not inlined", Assert.Single(rewritten.Warnings ?? []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    public void ANonApiRoleIsReturnedIdentically(string harness)
    {
        File.WriteAllText(Path.Combine(branch, "AGENTS.md"), "BRANCH\n");
        ResolvedRole original = Resolved(harness, main);

        ResolvedRole rewritten = Renderer.WithRulesFrom(original, branch);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void APromptWithoutAHouseRulesSectionIsARenderError()
    {
        ResolvedRole role = Resolved("api", main) with { SystemPrompt = "no sections here" };

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => Renderer.WithRulesFrom(role, branch));

        Assert.Contains("House rules", ex.Message, StringComparison.Ordinal);
    }
}
