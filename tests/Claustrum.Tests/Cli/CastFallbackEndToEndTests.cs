using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #126's cast verbs through the built binary (PATH emptied, so no backend is installed - the harness check only needs them registered): `cast questions`
// asks each fallback after everything else, `cast new` on a pipe keeps old eight-line scripts working (end of input at a fallback means none) and refuses
// a fallback on a harness the role does not list without shifting a later answer, `cast create` records resolved_fallback and refuses the same.
public sealed class CastFallbackEndToEndTests : IDisposable
{
    // The questions as they were before #126: architect, builder, code-reviewer, demo-author, tester, ui-reviewer, builder_max_parallel, budget.
    private const string EightAnswers = "spawned on claude:opus\nclaude:opus\nnot needed\nnot needed\nclaude:sonnet\nclaude:sonnet\n2\n10\n";

    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private readonly string emptyPath;

    public CastFallbackEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> RunAsync(string[] args, string stdin = "") => cli.RunAsync(args, stdin, emptyPath);

    private string DefaultCastPath => Path.Combine(cli.CastsDirectory, "default.json");

    private JsonElement SavedCast()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(DefaultCastPath));
        return document.RootElement.Clone();
    }

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    private async Task<CliResult> CastCreateAsync(string answersJson)
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), answersJson);
        return await RunAsync(["cast", "create", "--answers", "answers.json"]);
    }

    [Fact]
    public async Task CastQuestionsJsonAsksEveryFallbackLastWithNoneFirstAsync()
    {
        CliResult result = await RunAsync(["cast", "questions", "--json"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement[] questions = [.. document.RootElement.GetProperty("questions").EnumerateArray()];
        string[] keys = [.. questions.Select(question => question.GetProperty("key").GetString()!)];
        Assert.Equal(
            [
                "architect", "builder", "code-reviewer", "demo-author", "tester", "ui-reviewer", "builder_max_parallel", "budget",
                "architect_fallback", "builder_fallback", "code-reviewer_fallback", "demo-author_fallback", "tester_fallback", "ui-reviewer_fallback",
            ],
            keys);
        JsonElement reviewer = questions.Single(question => question.GetProperty("key").GetString() == "code-reviewer_fallback");
        Assert.Equal("none", reviewer.GetProperty("options")[0].GetString());
        Assert.True(reviewer.GetProperty("allow_free_form").GetBoolean());
        Assert.False(reviewer.GetProperty("allow_not_needed").GetBoolean());
        Assert.Contains("It runs on", reviewer.GetProperty("prompt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastQuestionsInTextAsksTheFallbacksAfterTheBudgetAsync()
    {
        CliResult result = await RunAsync(["cast", "questions"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.True(result.Stdout.IndexOf("budget:", StringComparison.Ordinal) < result.Stdout.IndexOf("architect_fallback:", StringComparison.Ordinal), result.Stdout);
        Assert.True(result.Stdout.IndexOf("architect_fallback:", StringComparison.Ordinal) < result.Stdout.IndexOf("ui-reviewer_fallback:", StringComparison.Ordinal), result.Stdout);
    }

    // The input of a script written before fallbacks: eight answers, then the end. Each fallback is "none", and no answer moved.
    [Fact]
    public async Task CastNewFedTheOldEightAnswersSavesEveryFallbackAsNoneAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], EightAnswers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement cast = SavedCast();
        JsonElement architect = cast.GetProperty("architect");
        Assert.Equal("spawned", Text(architect, "mode"));
        Assert.Equal("claude:opus", Text(architect, "model"));
        Assert.Equal(JsonValueKind.Null, architect.GetProperty("fallback").ValueKind);
        JsonElement roles = cast.GetProperty("roles");
        Assert.Equal("claude:opus", Text(roles.GetProperty("builder"), "model"));
        Assert.Equal(2, roles.GetProperty("builder").GetProperty("max_parallel").GetInt32());
        Assert.Equal("claude:sonnet", Text(roles.GetProperty("tester"), "model"));
        Assert.Equal("claude:sonnet", Text(roles.GetProperty("ui-reviewer"), "model"));
        foreach (string role in new[] { "builder", "tester", "ui-reviewer" })
        {
            Assert.Equal(JsonValueKind.Null, roles.GetProperty(role).GetProperty("fallback").ValueKind);
            Assert.Equal(JsonValueKind.Null, roles.GetProperty(role).GetProperty("resolved_fallback").ValueKind);
        }

        Assert.Equal(10m, cast.GetProperty("budget_usd").GetDecimal());
    }

    [Fact]
    public async Task CastNewWhoseInputEndsPartWayThroughTheFallbacksTakesNoneForTheRestAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], EightAnswers + "claude:sonnet\nclaude:haiku\n");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement cast = SavedCast();
        Assert.Equal("claude:sonnet", Text(cast.GetProperty("architect"), "fallback"));
        Assert.Equal("claude:haiku", Text(cast.GetProperty("roles").GetProperty("builder"), "fallback"));
        Assert.Equal(JsonValueKind.Null, cast.GetProperty("roles").GetProperty("tester").GetProperty("fallback").ValueKind);
    }

    [Fact]
    public async Task CastNewSavesEachFallbackAnswerToItsOwnRoleWithItsResolutionRecordedAsync()
    {
        // Fallbacks in the questions' order: architect, builder, code-reviewer (not needed: dropped), demo-author, tester, ui-reviewer.
        CliResult result = await RunAsync(["cast", "new"], EightAnswers + "claude:sonnet\nopencode:vendor/model\nclaude:haiku\nnone\nclaude:opus\nNONE\n");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement cast = SavedCast();
        JsonElement roles = cast.GetProperty("roles");
        Assert.Equal("claude:sonnet", Text(cast.GetProperty("architect"), "fallback"));
        Assert.Equal("claude:sonnet", Text(cast.GetProperty("architect"), "resolved_fallback"));
        Assert.Equal("opencode:vendor/model", Text(roles.GetProperty("builder"), "fallback"));
        Assert.Equal("opencode:vendor/model", Text(roles.GetProperty("builder"), "resolved_fallback"));
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("code-reviewer").ValueKind);
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("demo-author").ValueKind);
        Assert.Equal("claude:opus", Text(roles.GetProperty("tester"), "fallback"));
        Assert.Equal("claude:opus", Text(roles.GetProperty("tester"), "resolved_fallback"));
        Assert.Equal(JsonValueKind.Null, roles.GetProperty("ui-reviewer").GetProperty("fallback").ValueKind);
    }

    // An alias resolves like the model's own does, and the resolution is what is stored.
    [Fact]
    public async Task CastCreateRecordsWhatAFallbackAliasResolvedToAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"claude:opus","builder_fallback":"cheap-coding","tester":"claude:opus","tester_fallback":"claude:sonnet"}""");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement builder = SavedCast().GetProperty("roles").GetProperty("builder");
        Assert.Equal("cheap-coding", Text(builder, "fallback"));
        Assert.Equal("claude:haiku", Text(builder, "resolved_fallback"));
        Assert.Equal("claude:opus", Text(builder, "resolved"));
        JsonElement tester = SavedCast().GetProperty("roles").GetProperty("tester");
        Assert.Equal("claude:sonnet", Text(tester, "resolved_fallback"));
    }

    [Fact]
    public async Task CastCreateRefusesAFallbackOnAHarnessTheRoleDoesNotListExitingTwoAndSavingNothingAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"builder":"claude:opus","ui-reviewer":"claude:sonnet","ui-reviewer_fallback":"opencode:some-model"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cast 'default' not saved:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'opencode:some-model' puts ui-reviewer's fallback on 'opencode', but ui-reviewer runs on claude only", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("or 'none'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
        Assert.False(Directory.Exists(cli.CastsDirectory));
    }

    [Fact]
    public async Task CastCreateRefusesASpawnedArchitectsFallbackOnApiAsync()
    {
        CliResult result = await CastCreateAsync(/*lang=json,strict*/ """{"architect":"spawned on claude:opus","architect_fallback":"api:some-model","builder":"claude:opus"}""");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("puts architect's fallback on 'api'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }

    // On a pipe nobody can be asked again: the refused line ends the run, and no later answer slides up a question.
    [Fact]
    public async Task CastNewOnAPipeRefusesAFallbackOnTheWrongHarnessWithoutAskingAgainAsync()
    {
        // architect_fallback, builder_fallback, code-reviewer_fallback, demo-author_fallback (a claude-only role, on opencode), then the rest unread.
        CliResult result = await RunAsync(["cast", "new"], "host\nclaude:opus\nclaude:opus\nclaude:opus\nclaude:sonnet\nclaude:sonnet\n2\n10\nnone\nnone\nnone\nopencode:x\nnone\nnone\n");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cast not saved:", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("'opencode:x' puts demo-author's fallback on 'opencode'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("demo-author_fallback>", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("tester_fallback>", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }

    // End of input is only a gift at a fallback: at any other question it is still the #46 refusal.
    [Fact]
    public async Task EndOfInputBeforeTheBudgetIsStillARefusalNotNoneAsync()
    {
        CliResult result = await RunAsync(["cast", "new"], "host\nclaude:opus\nclaude:opus\nclaude:opus\nclaude:sonnet\nclaude:sonnet\n2\n");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("cast not saved: input ended before the answer to 'budget'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(DefaultCastPath));
    }
}
