using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// Issue #30: the owner decided on 2026-10-08 that ui-reviewer is frontier-coding and tester
// standard-coding at every tier, while code-reviewer keeps standard-coding -> frontier-coding at
// xhigh. A -xhigh/-max stub may only claim "identical ... model" when the model the harness FILE
// names for the tier equals the base agent's (not merely when the role.json class matches), so
// the wording is asserted against what each sync actually emitted.
public sealed class TierLadderTests : IDisposable
{
    private const string IdenticalWording = "identical role, model, and rules";
    private const string StepUpWording = "a model+effort step up";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-ladder-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-ladder-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private void Sync(string harness)
    {
        RoleRenderer renderer = new(library);
        _ = harness switch
        {
            "claude" => new ClaudeSync(library, renderer, fakeHome).Sync(cwd),
            "opencode" => new OpencodeSync(library, renderer, fakeHome).Sync(cwd),
            "cursor" => new CursorSync(library, renderer, fakeHome).Sync(cwd),
            "copilot" => new CopilotSync(library, renderer, fakeHome).Sync(cwd),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null),
        };
    }

    private string AgentFile(string harness, string name) => harness switch
    {
        "claude" => Path.Combine(cwd, ".claude", "agents", $"{name}.md"),
        "opencode" => Path.Combine(cwd, ".opencode", "agent", $"{name}.md"),
        "cursor" => Path.Combine(cwd, ".cursor", "agents", $"{name}.md"),
        "copilot" => Path.Combine(cwd, ".github", "agents", $"{name}.agent.md"),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null),
    };

    private string FrontmatterLine(string harness, string name, string key)
    {
        string prefix = $"{key}: ";
        return File.ReadAllLines(AgentFile(harness, name)).First(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
    }

    [Theory]
    [InlineData("builder", "xhigh")]
    [InlineData("builder", "max")]
    public void TierDescriptionWithSameModelKeepsTheOriginalWording(string role, string tier)
    {
        string description = SyncWriter.TierDescription(role, tier, sameModel: true);

        Assert.Contains(IdenticalWording, description, StringComparison.Ordinal);
        Assert.DoesNotContain("step up with", description, StringComparison.Ordinal);
        Assert.DoesNotContain(StepUpWording, description, StringComparison.Ordinal);
    }

    // The exact pre-#30 strings, so "byte-identical to the previous text" is pinned, not paraphrased.
    [Fact]
    public void TierDescriptionWithSameModelIsByteIdenticalToThePreviousText()
    {
        Assert.Equal(
            "Builder at EXTRA (xhigh) reasoning effort — identical role, model, and rules as the `builder` agent, "
            + "but thinks harder. Routine work → `builder`; the hardest cases → `builder-max`.",
            SyncWriter.TierDescription("builder", "xhigh", sameModel: true));
        Assert.Equal(
            "Builder at MAX reasoning effort — identical role, model, and rules as the `builder` agent, with the "
            + "deepest reasoning and no token-spend constraint. Reserve for genuinely hard, high-stakes, or "
            + "previously-stuck cases. For everyday work use `builder`; for a step up use `builder-xhigh`.",
            SyncWriter.TierDescription("builder", "max", sameModel: true));
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void TierDescriptionWithADifferentModelSaysStepUpAndNeverClaimsAnIdenticalModel(string tier)
    {
        string description = SyncWriter.TierDescription("code-reviewer", tier, sameModel: false);

        Assert.Contains(StepUpWording, description, StringComparison.Ordinal);
        Assert.Contains("identical role and rules", description, StringComparison.Ordinal);
        Assert.DoesNotContain("model, and rules", description, StringComparison.Ordinal);
        Assert.DoesNotContain("identical role, model", description, StringComparison.Ordinal);
        Assert.Contains("`code-reviewer`", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TierDescriptionRejectsATierWithoutATemplateWhateverSameModelIs()
    {
        Assert.Throws<RoleRenderException>(() => SyncWriter.TierDescription("builder", "high", sameModel: false));
        Assert.Throws<RoleRenderException>(() => SyncWriter.TierDescription("builder", "high", sameModel: true));
    }

    [Theory]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void ClaudeCodeReviewerStubsCarryTheStepUpDescriptionAndOpus(string tier)
    {
        Sync("claude");

        Assert.Equal("sonnet", FrontmatterLine("claude", "code-reviewer", "model"));
        Assert.Equal("opus", FrontmatterLine("claude", $"code-reviewer-{tier}", "model"));
        string description = FrontmatterLine("claude", $"code-reviewer-{tier}", "description");
        Assert.Contains(StepUpWording, description, StringComparison.Ordinal);
        Assert.DoesNotContain(IdenticalWording, description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("architect", "opus")]
    [InlineData("builder", "opus")]
    [InlineData("tester", "sonnet")]
    [InlineData("ui-reviewer", "opus")]
    public void ClaudeStubsWhoseModelDoesNotChangeKeepTheOriginalWordingAtBothTiers(string role, string model)
    {
        Sync("claude");

        foreach (string tier in new[] { "xhigh", "max" })
        {
            Assert.Equal(model, FrontmatterLine("claude", role, "model"));
            Assert.Equal(model, FrontmatterLine("claude", $"{role}-{tier}", "model"));
            string description = FrontmatterLine("claude", $"{role}-{tier}", "description");
            Assert.Contains(IdenticalWording, description, StringComparison.Ordinal);
            Assert.DoesNotContain(StepUpWording, description, StringComparison.Ordinal);
            Assert.Equal(SyncWriter.YamlQuoted(SyncWriter.TierDescription(role, tier, sameModel: true)), description);
        }
    }

    // Claude's ladder is the only one where a class change shows up in the emitted model line:
    // opencode maps all three coding classes to one model, cursor emits `inherit`, copilot `auto`.
    [Theory]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void EveryStubOnTheOtherHarnessesKeepsTheOriginalWordingBecauseTheEmittedModelNeverDiffers(string harness)
    {
        Sync(harness);

        string[] roles = ["architect", "builder", "code-reviewer", "tester"];
        foreach (string role in roles)
        {
            foreach (string tier in new[] { "xhigh", "max" })
            {
                string description = FrontmatterLine(harness, $"{role}-{tier}", "description");
                Assert.Contains(IdenticalWording, description, StringComparison.Ordinal);
                Assert.DoesNotContain(StepUpWording, description, StringComparison.Ordinal);
                Assert.Equal(SyncWriter.YamlQuoted(SyncWriter.TierDescription(role, tier, sameModel: true)), description);
            }
        }
    }

    [Fact]
    public void OpencodeStubsRenderTheSameModelLineAsTheirBaseAgent()
    {
        Sync("opencode");

        foreach (string role in new[] { "architect", "builder", "code-reviewer", "tester" })
        {
            string baseModel = FrontmatterLine("opencode", role, "model");
            Assert.Equal(baseModel, FrontmatterLine("opencode", $"{role}-xhigh", "model"));
            Assert.Equal(baseModel, FrontmatterLine("opencode", $"{role}-max", "model"));
        }
    }

    [Theory]
    [InlineData("tester")]
    [InlineData("ui-reviewer")]
    public void ClaudeTesterAndUiReviewerStubsRenderTheSameModelAsTheirHighTier(string role)
    {
        Sync("claude");

        string baseModel = FrontmatterLine("claude", role, "model");
        Assert.Equal(baseModel, FrontmatterLine("claude", $"{role}-xhigh", "model"));
        Assert.Equal(baseModel, FrontmatterLine("claude", $"{role}-max", "model"));
        Assert.Equal("xhigh", FrontmatterLine("claude", $"{role}-xhigh", "effort"));
        Assert.Equal("max", FrontmatterLine("claude", $"{role}-max", "effort"));
    }

    [Fact]
    public void OpencodeTesterStubsRenderTheSameModelAsTheirHighTierAndUiReviewerIsNotRenderedThere()
    {
        Sync("opencode");

        string baseModel = FrontmatterLine("opencode", "tester", "model");
        Assert.Equal(baseModel, FrontmatterLine("opencode", "tester-xhigh", "model"));
        Assert.Equal(baseModel, FrontmatterLine("opencode", "tester-max", "model"));
        Assert.False(File.Exists(AgentFile("opencode", "ui-reviewer")));
    }

    // The ladders themselves, as the owner decided them (2026-10-08), straight from role.json.
    [Theory]
    [InlineData("ui-reviewer", "frontier-coding", "frontier-coding", "frontier-coding")]
    [InlineData("tester", "standard-coding", "standard-coding", "standard-coding")]
    [InlineData("code-reviewer", "standard-coding", "frontier-coding", "frontier-coding")]
    public void RoleJsonLaddersFollowTheOwnersDecision(string role, string high, string xhigh, string max)
    {
        RoleDefinition definition = library.LoadRole(role, cwd).Definition;

        Assert.Equal(high, definition.Tiers["high"].Model);
        Assert.Equal(xhigh, definition.Tiers["xhigh"].Model);
        Assert.Equal(max, definition.Tiers["max"].Model);
        Assert.Equal("high", definition.Tiers["high"].Effort);
        Assert.Equal("xhigh", definition.Tiers["xhigh"].Effort);
        Assert.Equal("max", definition.Tiers["max"].Effort);
    }

    [Fact]
    public void ClaudeSyncCheckAfterSyncReportsNothingOutstanding()
    {
        Sync("claude");
        RoleRenderer renderer = new(library);

        SyncResult check = new ClaudeSync(library, renderer, fakeHome).Sync(cwd, mode: SyncMode.Check);

        Assert.Empty(check.Written);
        Assert.Empty(check.Foreign);
        Assert.NotEmpty(check.Skipped);
    }
}
