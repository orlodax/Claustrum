using Claustrum.Casts;

namespace Claustrum.Tests.Casts;

public sealed class CastBuilderTests
{
    private static readonly string[] roleNames = ["builder", "code-reviewer", "tester"];

    [Fact]
    public void BuildsACastFromACompleteAnswerSet()
    {
        Dictionary<string, string> answers = new()
        {
            ["architect"] = "host",
            ["builder"] = "opencode:openrouter/deepseek/deepseek-v4-pro",
            ["code-reviewer"] = "claude:opus",
            ["tester"] = "not needed",
            ["budget"] = "10",
        };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Equal("default", cast.Name);
        Assert.Equal("1.0.0", cast.Library);
        Assert.Equal("host", cast.Architect.Mode);
        Assert.Equal("opencode:openrouter/deepseek/deepseek-v4-pro", cast.Roles["builder"]!.Model);
        Assert.Equal("claude:opus", cast.Roles["code-reviewer"]!.Model);
        Assert.Null(cast.Roles["tester"]);
        Assert.Equal(10m, cast.BudgetUsd);
    }

    [Fact]
    public void NoCapBudgetAnswerBecomesNull()
    {
        Dictionary<string, string> answers = new() { ["budget"] = "no cap" };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Null(cast.BudgetUsd);
    }

    [Fact]
    public void MissingBudgetAnswerAlsoBecomesUnlimited()
    {
        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, new Dictionary<string, string>());

        Assert.Null(cast.BudgetUsd);
    }

    [Fact]
    public void UnparsableBudgetAnswerThrowsCastException()
    {
        Dictionary<string, string> answers = new() { ["budget"] = "lots" };

        Assert.Throws<CastException>(() => CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers));
    }

    [Fact]
    public void MissingArchitectAnswerDefaultsToHost()
    {
        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, new Dictionary<string, string>());

        Assert.Equal("host", cast.Architect.Mode);
    }

    [Theory]
    [InlineData("not needed")]
    [InlineData("NOT NEEDED")]
    [InlineData("")]
    public void NotNeededRoleAnswerVariantsAllBecomeNull(string answer)
    {
        Dictionary<string, string> answers = new() { ["code-reviewer"] = answer };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Null(cast.Roles["code-reviewer"]);
    }

    [Fact]
    public void RoleNamesDriveTheRolesDictionaryNotAHardcodedList()
    {
        Dictionary<string, string> answers = new() { ["ui-reviewer"] = "claude:opus" };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", ["ui-reviewer"], answers);

        Assert.Equal("claude:opus", cast.Roles["ui-reviewer"]!.Model);
        Assert.False(cast.Roles.ContainsKey("builder"));
    }

    // The library now ships a real "architect" role (roles/architect/role.json), so the loop that
    // walks roleNames has to skip it rather than throw: its answer is the fixed host/spawned
    // question and lands in Cast.Architect, never as a second entry in Cast.Roles.
    [Fact]
    public void ARoleNamedArchitectInRoleNamesIsSkippedAndNeverEntersRoles()
    {
        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", ["architect", "builder"], new Dictionary<string, string> { ["builder"] = "claude:opus" });

        Assert.False(cast.Roles.ContainsKey("architect"));
        Assert.Equal("claude:opus", cast.Roles["builder"]!.Model);
    }

    [Theory]
    [InlineData("host", CastArchitect.Host, null)]
    [InlineData("spawned", CastArchitect.Spawned, null)]
    [InlineData("spawned on cheap-coding", CastArchitect.Spawned, "cheap-coding")]
    public void ArchitectAnswerVariantsParseToTheExpectedCastArchitect(string answer, string expectedMode, string? expectedModel)
    {
        Dictionary<string, string> answers = new() { ["architect"] = answer };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Equal(expectedMode, cast.Architect.Mode);
        Assert.Equal(expectedModel, cast.Architect.Model);
    }

    [Fact]
    public void AnUnrecognisedArchitectAnswerThrowsCastException()
    {
        Dictionary<string, string> answers = new() { ["architect"] = "some junk" };

        Assert.Throws<CastException>(() => CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers));
    }

    // Review finding: docs/PLAN.md §D2 asks for "builder model + max_parallel", but CastBuilder never
    // set MaxParallel, so worktree isolation was unreachable without hand-editing the cast JSON.
    [Fact]
    public void BuilderMaxParallelAnswerLandsOnTheBuilderEntry()
    {
        Dictionary<string, string> answers = new()
        {
            ["builder"] = "claude:opus",
            ["code-reviewer"] = CastBuilder.NotNeeded,
            ["tester"] = CastBuilder.NotNeeded,
            [CastQuestionnaire.MaxParallelKey] = "3",
            ["budget"] = CastBuilder.NoCap,
        };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Equal(3, cast.Roles["builder"]!.MaxParallel);
    }

    // 1 means "no isolation", spelled as null so `cast show` does not imply a setting nobody made.
    [Theory]
    [InlineData("1")]
    [InlineData("")]
    public void OneOrNoAnswerLeavesMaxParallelUnset(string answer)
    {
        Dictionary<string, string> answers = new()
        {
            ["builder"] = "claude:opus",
            ["code-reviewer"] = CastBuilder.NotNeeded,
            ["tester"] = CastBuilder.NotNeeded,
            [CastQuestionnaire.MaxParallelKey] = answer,
            ["budget"] = CastBuilder.NoCap,
        };

        Cast cast = CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers);

        Assert.Null(cast.Roles["builder"]!.MaxParallel);
    }

    [Fact]
    public void ANonNumericMaxParallelAnswerIsRejectedByName()
    {
        Dictionary<string, string> answers = new()
        {
            ["builder"] = "claude:opus",
            ["code-reviewer"] = CastBuilder.NotNeeded,
            ["tester"] = CastBuilder.NotNeeded,
            [CastQuestionnaire.MaxParallelKey] = "lots",
            ["budget"] = CastBuilder.NoCap,
        };

        CastException ex = Assert.Throws<CastException>(() => CastBuilder.FromAnswers("default", "1.0.0", roleNames, answers));

        Assert.Contains("lots", ex.Message, StringComparison.Ordinal);
    }
}
