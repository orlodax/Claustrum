namespace Claustrum.Roles.Tests;

// Role text ported from the team's devkit (PR #42): "Clean up what you start" in every role, builders
// that never touch a test file, a tester that runs the repo's full gate, and an architect that spawns
// the demo-author once the gate is green. Each assertion is one rule the port added; phrases are matched
// on flattened prose (Prose.Flatten) so a re-wrap does not fail them.
public sealed class DevkitRolePortTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-devkit-port-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private string Body(string role, string harness, string tier = "high") =>
        Prose.Flatten(new RoleRenderer(library).Render(role, tier, harness, cwd).SystemBody);

    [Fact]
    public void EveryRoleOnEveryHarnessItListsHasExactlyOneCleanUpWhatYouStartSection()
    {
        foreach (string role in library.ListRoles())
        {
            foreach (string harness in library.LoadRole(role, cwd).Definition.Harnesses)
            {
                string body = Body(role, harness);

                Assert.True(CountOf(body, "## Clean up what you start") == 1, $"role '{role}' on '{harness}': expected one 'Clean up what you start' section");
                Assert.Contains("Every process you open, you close.", body, StringComparison.Ordinal);
                Assert.Contains("orphaned child", body, StringComparison.Ordinal);
            }
        }
    }

    // The section reaches the architect as "(non-negotiable)" and every other role plainly; each also
    // says what to do when a process must stay up, so the next stage is told rather than surprised.
    [Theory]
    [InlineData("architect", "## Clean up what you start (non-negotiable)")]
    [InlineData("builder", "Servers, containers, watchers, dev processes and background jobs you launched")]
    [InlineData("tester", "name it.")]
    [InlineData("ui-reviewer", "Close your browser sessions too.")]
    [InlineData("demo-author", "browsers and any temporary daemon you launched")]
    [InlineData("code-reviewer", "to inspect a change")]
    public void EachRolesCleanUpSectionNamesWhatThatRoleStarts(string role, string phrase) =>
        Assert.Contains(phrase, Body(role, "claude"), StringComparison.Ordinal);

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheBuilderIsForbiddenEveryTestFileChangeOnEveryHarness(string harness)
    {
        string body = Body("builder", harness);

        Assert.Contains("never write, modify, or delete any test file — new or pre-existing", body, StringComparison.Ordinal);
        Assert.Contains("not even test fixtures, scaffolding", body, StringComparison.Ordinal);
        Assert.Contains("`behaviour_to_cover`", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void TheBuilderTierStubsRepeatTheTestFileBanInTheirNonNegotiables(string tier)
    {
        string stub = Prose.Flatten(new RoleRenderer(library).RenderTierStub("builder", tier, cwd));

        Assert.Contains("never create, modify or delete a test file — pre-existing ones and fixtures included", stub, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheTesterRunsTheFullGateOnEveryHarness(string harness)
    {
        string body = Body("tester", harness);

        Assert.Contains("You write the tests AND you run the repo's full gate.", body, StringComparison.Ordinal);
        Assert.Contains("every suite it includes, the pre-existing tests as well as the ones you wrote", body, StringComparison.Ordinal);
        Assert.Contains("A run of only the new tests is not a pass", body, StringComparison.Ordinal);
        Assert.Contains("`fault_in: \"test\"` only when the test itself was wrong — one you wrote, or a pre-existing one the change legitimately outdated", body, StringComparison.Ordinal);
        Assert.Contains("`commands_run` must show the repo's full gate", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTesterTierStubsAndReportContractRepeatTheFullGateRule()
    {
        foreach (string tier in new[] { "xhigh", "max" })
        {
            string stub = Prose.Flatten(new RoleRenderer(library).RenderTierStub("tester", tier, cwd));
            Assert.Contains("full gate, pre-existing tests included", stub, StringComparison.Ordinal);
        }

        string contract = Prose.Flatten(library.ReadShared("_shared/report/tester.md"));
        Assert.Contains("a pre-existing one the change legitimately outdated", contract, StringComparison.Ordinal);
        Assert.Contains("pre-existing suites included, not only the tests you wrote", contract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("the only line it ever puts there")]
    [InlineData("a reminder may go in `## Context`, for non-blind roles only")]
    [InlineData("The `demo-author` is the opposite case: it gets the rationale too")]
    [InlineData("delegate to a **demo-author** once the gate is green")]
    public void TheArchitectOnEveryHarnessSpawnsTheDemoAuthorUnderTheRulesThePortAdded(string phrase)
    {
        foreach (string harness in library.LoadRole("architect", cwd).Definition.Harnesses)
            Assert.Contains(phrase, Body("architect", harness), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void TheArchitectTierStubsKeepTheSightedDemoAuthorAndTheTestFileBanInTheirNonNegotiables(string tier)
    {
        string stub = Prose.Flatten(new RoleRenderer(library).RenderTierStub("architect", tier, cwd));

        Assert.Contains("tester → demo-author for every browser-facing feature", stub, StringComparison.Ordinal);
        Assert.Contains("never touch a test file", stub, StringComparison.Ordinal);
        Assert.Contains("Only the demo-author is briefed sighted", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchitectNamesTheNativeDemoAuthorSpawnOnClaudeAndTheCliCallElsewhere()
    {
        Assert.Contains("demo-author → the `Agent` tool with `subagent_type: \"demo-author\"` (`run_in_background: false` to block on the result)", Body("architect", "claude"), StringComparison.Ordinal);

        string opencode = Body("architect", "opencode");
        Assert.DoesNotContain("subagent_type", opencode, StringComparison.Ordinal);
        Assert.Contains("claustrum run <role> --brief-file <path> --json", opencode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stage's commit")]
    [InlineData("commits on hold")]
    public void TheArchitectDoesNotCarryTextAnEarlierRoundRemoved(string phrase)
    {
        foreach (string harness in library.LoadRole("architect", cwd).Definition.Harnesses)
            Assert.DoesNotContain(phrase, Body("architect", harness), StringComparison.Ordinal);
    }

    // The skill tells every harness's agent the same pipeline the architect runs, including the new
    // last stage and who gets an `## Access` section.
    [Fact]
    public void TheSkillNamesTheDemoAuthorStageAndItsAccessSection()
    {
        string skill = Prose.Flatten(library.ReadShared("_shared/claustrum-skill.md"));

        Assert.Contains("`## Access` (ui-reviewer and demo-author:", skill, StringComparison.Ordinal);
        Assert.Contains("tester (→ demo-author, for a browser-facing feature)", skill, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string phrase)
    {
        int count = 0;
        for (int index = text.IndexOf(phrase, StringComparison.Ordinal); index >= 0; index = text.IndexOf(phrase, index + phrase.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
