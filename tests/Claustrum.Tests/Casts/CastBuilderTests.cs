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

    [Fact]
    public void ARoleNamedArchitectThrowsInsteadOfCollidingWithTheModeQuestion()
    {
        Assert.Throws<CastException>(() => CastBuilder.FromAnswers("default", "1.0.0", ["architect"], new Dictionary<string, string>()));
    }
}
