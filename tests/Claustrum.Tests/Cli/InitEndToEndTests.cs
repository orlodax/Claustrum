using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #122 `claustrum init` through the built binary: the model aliases are per-developer and live in the USER config (never in a repo
// claustrum.json), `init questions --json` asks about exactly the built-in aliases that file does not define, `--answers` validates
// and merges only what is missing, and the repo side is the scaffold, the `.claustrum/casts/` ignore rule and the AGENTS.md pointer.
public sealed class InitEndToEndTests : IDisposable
{
    private static readonly string[] aliases = ["frontier-reasoning", "frontier-coding", "standard-coding", "cheap-coding", "fast"];

    private readonly ClaustrumCli cli = new();

    public void Dispose() => cli.Dispose();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> InitAsync(params string[] extra) => cli.RunAsync(["init", .. extra], "", null);

    private string WriteAnswers(string json, string name = "answers.json")
    {
        string path = Path.Combine(cli.Cwd, name);
        File.WriteAllText(path, json);
        return path;
    }

    private JsonElement ReadUserConfig()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(cli.UserConfigPath));
        return document.RootElement.Clone();
    }

    private static string[] KeysOf(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    // The questionnaire's property casing is not part of what is pinned here; the keys and the path are.
    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private async Task<(string[] Keys, string UserConfigPath)> QuestionsAsync()
    {
        CliResult result = await cli.RunAsync(["init", "questions", "--json"], "", null);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        string[] keys = [.. Property(document.RootElement, "questions").EnumerateArray().Select(question => Property(question, "key").GetString()!)];
        return (keys, Property(document.RootElement, "userConfigPath").GetString()!);
    }

    // ---- init questions ---------------------------------------------------------------------------------------------

    // Regression F3 (2026-10-15): Merged.Models always holds the built-ins, so asking it made every alias look answered and the
    // questionnaire listed none.
    [Fact]
    public async Task WithNoUserConfigTheQuestionnaireAsksAboutEveryBuiltInAliasAndNamesTheFileItWillWriteAsync()
    {
        (string[] keys, string path) = await QuestionsAsync();

        Assert.Equal(aliases.Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
        Assert.Equal(cli.UserConfigPath, path);
    }

    [Fact]
    public async Task TheQuestionnaireListsExactlyTheAliasesTheUserConfigLacksAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:sonnet","cheap-coding":"claude:haiku"}}""");

        (string[] keys, _) = await QuestionsAsync();

        Assert.Equal(["frontier-coding", "frontier-reasoning", "standard-coding"], keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WhenTheUserConfigDefinesEveryAliasTheQuestionnaireIsEmptyAsync()
    {
        cli.WriteUserConfig();

        (string[] keys, _) = await QuestionsAsync();

        Assert.Empty(keys);
    }

    [Fact]
    public async Task EachQuestionOffersTheBuiltInTargetAndAllowsAFreeFormAnswerAsync()
    {
        CliResult result = await cli.RunAsync(["init", "questions", "--json"], "", null);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement cheap = Property(document.RootElement, "questions").EnumerateArray().Single(question => Property(question, "key").GetString() == "cheap-coding");
        Assert.Equal(["claude:haiku"], Property(cheap, "options").EnumerateArray().Select(option => option.GetString()));
        Assert.True(Property(cheap, "allowFreeForm").GetBoolean());
        Assert.False(Property(cheap, "allowNotNeeded").GetBoolean());
    }

    // An alias the user config defines but a repo claustrum.json overrides is still the user's: nothing to ask about.
    [Fact]
    public async Task AnAliasTheUserConfigDefinesIsNotAskedAboutJustBecauseARepoFileOverridesItAsync()
    {
        cli.WriteUserConfig();
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:vendor/model"}}""");

        (string[] keys, _) = await QuestionsAsync();

        Assert.Empty(keys);
    }

    [Fact]
    public async Task TheTextFormPrintsOneLinePerQuestionAndItsOptionsAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:haiku","cheap-coding":"claude:haiku","standard-coding":"claude:sonnet","frontier-coding":"claude:opus"}}""");

        CliResult result = await cli.RunAsync(["init", "questions"], "", null);

        string[] lines = Lf(result.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("frontier-reasoning: frontier-reasoning:", lines[0], StringComparison.Ordinal);
        Assert.Contains("claude:opus", lines[1], StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);
    }

    // ---- init --answers -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnswersAreWrittenToTheUserConfigAndNeverToARepoClaustrumJsonAsync()
    {
        string answers = WriteAnswers(/*lang=json,strict*/ """
            {"frontier-reasoning":"claude:opus","frontier-coding":"opencode:vendor/coder","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"}
            """);

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement models = ReadUserConfig().GetProperty("models");
        Assert.Equal(aliases.Order(StringComparer.Ordinal), KeysOf(models).Order(StringComparer.Ordinal));
        Assert.Equal("opencode:vendor/coder", models.GetProperty("frontier-coding").GetString());
        Assert.False(File.Exists(Path.Combine(cli.Cwd, "claustrum.json")));
        Assert.Contains("claustrum.json: not written (aliases are per-developer, in ", Lf(result.Stdout), StringComparison.Ordinal);
        (string[] keys, _) = await QuestionsAsync();
        Assert.Empty(keys);
    }

    [Fact]
    public async Task AnUnknownBackendInAnAnswerIsExitTwoNamingTheAliasAndNothingIsWrittenAsync()
    {
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"claude:haiku","cheap-coding":"nosuchbackend:model"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("alias 'cheap-coding': unknown backend 'nosuchbackend' in 'nosuchbackend:model'", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(cli.UserConfigPath), "a refused answer set writes none of its answers");
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum")), "and scaffolds nothing");
    }

    [Fact]
    public async Task AnAnswerForAKeyThatIsNoBuiltInAliasIsExitTwoListingTheValidKeysAsync()
    {
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"claude:haiku","made-up":"claude:opus"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("unknown alias 'made-up' in answers (valid keys: frontier-reasoning, frontier-coding, standard-coding, cheap-coding, fast)", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(cli.UserConfigPath));
    }

    [Fact]
    public async Task ABareModelIdIsTakenAsAClaudeModelAndIsValidAsync()
    {
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"haiku"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal("haiku", ReadUserConfig().GetProperty("models").GetProperty("fast").GetString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData(/*lang=json,strict*/ """{"fast":5}""")]
    public async Task AnAnswersFileThatIsNotAnAliasToStringObjectIsExitTwoAsync(string text)
    {
        string answers = WriteAnswers(text);

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(answers, result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(cli.UserConfigPath));
    }

    [Fact]
    public async Task AMissingAnswersFileIsExitTwoNamingItAsync()
    {
        string path = Path.Combine(cli.Cwd, "nowhere.json");

        CliResult result = await InitAsync("--answers", path);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(path, result.Stderr, StringComparison.Ordinal);
    }

    // Merges only what is missing and never overwrites: an answer for an alias the user config already defines is ignored.
    [Fact]
    public async Task AnAnswerForAnAliasTheUserConfigAlreadyDefinesIsIgnoredAndTheOthersAreAddedAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:sonnet"}}""");
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"claude:opus","cheap-coding":"claude:haiku"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement models = ReadUserConfig().GetProperty("models");
        Assert.Equal("claude:sonnet", models.GetProperty("fast").GetString());
        Assert.Equal("claude:haiku", models.GetProperty("cheap-coding").GetString());
        Assert.Equal(2, KeysOf(models).Length);
    }

    [Fact]
    public async Task AnAnswerSetThatAddsNothingLeavesTheUserConfigByteIdenticalAsync()
    {
        string path = cli.WriteUserConfig();
        string before = File.ReadAllText(path);
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"claude:opus","cheap-coding":"claude:opus"}""");
        DateTime written = File.GetLastWriteTimeUtc(path);

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task EveryOtherKeyOfTheUserConfigSurvivesTheMergeAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """
            {
              "models": { "fast": "claude:sonnet" },
              "roles": { "builder": { "model": "fast", "effort": "high", "permission": "edit+shell", "deny": ["git push"] } },
              "backends": { "claude": { "path": "/opt/claude" } },
              "defaults": { "timeout_seconds": 900, "budget_usd": 3.5, "env_passthrough": "all" },
              "jobs": { "keep_last": 7 }
            }
            """);
        string answers = WriteAnswers(/*lang=json,strict*/ """{"cheap-coding":"claude:haiku"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement config = ReadUserConfig();
        Assert.Equal("claude:sonnet", config.GetProperty("models").GetProperty("fast").GetString());
        Assert.Equal("claude:haiku", config.GetProperty("models").GetProperty("cheap-coding").GetString());
        JsonElement builder = config.GetProperty("roles").GetProperty("builder");
        Assert.Equal("fast", builder.GetProperty("model").GetString());
        Assert.Equal("high", builder.GetProperty("effort").GetString());
        Assert.Equal("edit+shell", builder.GetProperty("permission").GetString());
        Assert.Equal(["git push"], builder.GetProperty("deny").EnumerateArray().Select(entry => entry.GetString()));
        Assert.Equal("/opt/claude", config.GetProperty("backends").GetProperty("claude").GetProperty("path").GetString());
        JsonElement defaults = config.GetProperty("defaults");
        Assert.Equal(900, defaults.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal(3.5m, defaults.GetProperty("budget_usd").GetDecimal());
        Assert.Equal("all", defaults.GetProperty("env_passthrough").GetString());
        Assert.Equal(7, config.GetProperty("jobs").GetProperty("keep_last").GetInt32());
    }

    [Fact]
    public async Task MissingAliasesWithNoAnswersAndNoTerminalIsExitTwoCountingThemAndScaffoldingNothingAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:haiku","cheap-coding":"claude:haiku"}}""");

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal($"model aliases: 3 missing from {cli.UserConfigPath}; answer `claustrum init questions --json` and run `claustrum init --answers <file>`", Lf(result.Stderr).Trim());
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum")));
    }

    [Fact]
    public async Task AnAnswersFileThatIsAFifoIsRefusedWithoutBlockingAsync()
    {
        string fifo = Path.Combine(cli.Cwd, "answers.fifo");
        PipeFiles.MakeFifo(fifo);

        CliResult result = await InitAsync("--answers", fifo);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("not a regular file", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(cli.UserConfigPath));
    }

    // ---- what init does to the repository ------------------------------------------------------------------------------

    [Fact]
    public async Task InitWritesNoClaustrumJsonAndIsIdempotentAsync()
    {
        cli.WriteUserConfig();
        File.WriteAllText(Path.Combine(cli.Cwd, "AGENTS.md"), "# House rules\n");

        CliResult first = await InitAsync();
        string gitignore = File.ReadAllText(Path.Combine(cli.Cwd, ".gitignore"));
        string agents = File.ReadAllText(Path.Combine(cli.Cwd, "AGENTS.md"));
        string userConfig = File.ReadAllText(cli.UserConfigPath);
        CliResult second = await InitAsync();

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.Equal(ExitCodes.Ok, second.ExitCode);
        Assert.False(File.Exists(Path.Combine(cli.Cwd, "claustrum.json")));
        Assert.Equal(gitignore, File.ReadAllText(Path.Combine(cli.Cwd, ".gitignore")));
        Assert.Equal(agents, File.ReadAllText(Path.Combine(cli.Cwd, "AGENTS.md")));
        Assert.Equal(userConfig, File.ReadAllText(cli.UserConfigPath));
        Assert.Contains(".gitignore: updated", Lf(first.Stdout), StringComparison.Ordinal);
        Assert.Contains(".gitignore: already up to date", Lf(second.Stdout), StringComparison.Ordinal);
        Assert.Contains("AGENTS.md: pointer appended", Lf(first.Stdout), StringComparison.Ordinal);
        Assert.Contains("AGENTS.md: unchanged", Lf(second.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshGitignoreIgnoresTheCastsAndTheMachineryDirectoriesAsync()
    {
        cli.WriteUserConfig();

        await InitAsync();

        string[] lines = Lf(File.ReadAllText(Path.Combine(cli.Cwd, ".gitignore"))).Split('\n');
        Assert.Contains(".claustrum/casts/", lines);
        Assert.Contains(".claustrum/worktrees/", lines);
        Assert.Contains(".claustrum/briefs/", lines);
        Assert.Contains(".claustrum/locks/", lines);
        Assert.True(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum", "casts")));
    }

    [Fact]
    public async Task AnOldGitignoreWithTheThreeMachineryLinesGainsOnlyTheCastsLineAsync()
    {
        cli.WriteUserConfig();
        string path = Path.Combine(cli.Cwd, ".gitignore");
        File.WriteAllText(path, "bin/\n.claustrum/worktrees/\n.claustrum/briefs/\n.claustrum/locks/\n");

        CliResult result = await InitAsync();

        string[] lines = Lf(File.ReadAllText(path)).Split('\n');
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(1, lines.Count(line => line == ".claustrum/casts/"));
        Assert.Equal(1, lines.Count(line => line == ".claustrum/worktrees/"));
        Assert.StartsWith("bin/\n.claustrum/worktrees/\n.claustrum/briefs/\n.claustrum/locks/\n", Lf(File.ReadAllText(path)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastsThatGitStillTracksAreReportedWithTheCommandToUntrackThemAsync()
    {
        cli.WriteUserConfig();
        TestGit.Init(cli.Cwd);
        Directory.CreateDirectory(cli.CastsDirectory);
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "default.json"), CastLinkFixture.ValidCast);
        File.WriteAllText(Path.Combine(cli.CastsDirectory, "team.json"), CastLinkFixture.ValidCast);
        TestGit.CommitAll(cli.Cwd, "casts");

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains(".claustrum/casts/: 2 cast(s) still tracked; the new ignore rule does not untrack them, run `git rm --cached -r .claustrum/casts` if they are yours alone", Lf(result.Stderr), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTrackedCastsMeansNoNoteAsync()
    {
        cli.WriteUserConfig();
        TestGit.Init(cli.Cwd);
        File.WriteAllText(Path.Combine(cli.Cwd, "seed.txt"), "x\n");
        TestGit.CommitAll(cli.Cwd, "seed");

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("still tracked", result.Stderr, StringComparison.Ordinal);
    }
}
