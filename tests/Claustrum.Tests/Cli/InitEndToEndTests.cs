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

    // Every built-in alias answered, with these overrides: init exits 0 only on a complete set (2026-10-15, review 2).
    private static string CompleteAnswers(params (string Alias, string Target)[] overrides)
    {
        Dictionary<string, string> answers = new()
        {
            ["frontier-reasoning"] = "claude:opus",
            ["frontier-coding"] = "claude:opus",
            ["standard-coding"] = "claude:sonnet",
            ["cheap-coding"] = "claude:haiku",
            ["fast"] = "claude:haiku",
        };
        foreach ((string alias, string target) in overrides)
            answers[alias] = target;
        return JsonSerializer.Serialize(answers);
    }

    private static string[] MissingFrom(string stderr) => [.. Lf(stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("model aliases:", StringComparison.Ordinal))];

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

    // Everything under the child's cwd, so a refused run can be shown to have left no trace there.
    private string[] RepoEntries() => [.. Directory.EnumerateFileSystemEntries(cli.Cwd, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(cli.Cwd, path)).Order(StringComparer.Ordinal)];

    private static string[] KeysOf(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];

    // `init questions --json` is snake_case like every other document Claustrum writes (2026-10-15, review of #127): the name is matched exactly.
    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => property.Name == name).Value;

    private async Task<(string[] Keys, string UserConfigPath)> QuestionsAsync()
    {
        CliResult result = await cli.RunAsync(["init", "questions", "--json"], "", null);
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        string[] keys = [.. Property(document.RootElement, "questions").EnumerateArray().Select(question => Property(question, "key").GetString()!)];
        return (keys, Property(document.RootElement, "user_config_path").GetString()!);
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
        Assert.True(Property(cheap, "allow_free_form").GetBoolean());
        Assert.False(Property(cheap, "allow_not_needed").GetBoolean());
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
    public async Task EveryPropertyNameOfTheQuestionnaireJsonIsSnakeCaseAsync()
    {
        CliResult result = await cli.RunAsync(["init", "questions", "--json"], "", null);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        List<string> names = [];
        Collect(document.RootElement, names);
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Matches("^[a-z]+(_[a-z]+)*$", name));
        Assert.Contains("user_config_path", names);
        Assert.Contains("allow_free_form", names);

        static void Collect(JsonElement element, List<string> into)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    into.Add(property.Name);
                    Collect(property.Value, into);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in element.EnumerateArray())
                    Collect(item, into);
        }
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
        string answers = WriteAnswers(CompleteAnswers(("fast", "haiku")));

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
        string answers = WriteAnswers(CompleteAnswers(("fast", "claude:opus")));

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement models = ReadUserConfig().GetProperty("models");
        Assert.Equal("claude:sonnet", models.GetProperty("fast").GetString());
        Assert.Equal("claude:haiku", models.GetProperty("cheap-coding").GetString());
        Assert.Equal(aliases.Order(StringComparer.Ordinal), KeysOf(models).Order(StringComparer.Ordinal));
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
        string answers = WriteAnswers(CompleteAnswers(("fast", "claude:opus")));

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

    // Merged at the JSON level (2026-10-15, review of #127): re-serializing the typed document dropped what Claustrum does not model.
    [Fact]
    public async Task KeysClaustrumDoesNotModelSurviveTheMergeAtEveryLevelAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """
            {
              "$schema": "https://example.invalid/claustrum.schema.json",
              "note": "mine, do not touch",
              "models": { "fast": "claude:sonnet", "my-own-alias": "claude:opus" },
              "future_section": { "deep": [1, 2, { "x": null }] }
            }
            """);
        string answers = WriteAnswers(CompleteAnswers());

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        JsonElement config = ReadUserConfig();
        Assert.Equal("https://example.invalid/claustrum.schema.json", config.GetProperty("$schema").GetString());
        Assert.Equal("mine, do not touch", config.GetProperty("note").GetString());
        Assert.Equal("claude:opus", config.GetProperty("models").GetProperty("my-own-alias").GetString());
        Assert.Equal("claude:sonnet", config.GetProperty("models").GetProperty("fast").GetString());
        Assert.Equal("claude:haiku", config.GetProperty("models").GetProperty("cheap-coding").GetString());
        Assert.Equal(3, config.GetProperty("future_section").GetProperty("deep").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("future_section").GetProperty("deep")[2].GetProperty("x").ValueKind);
    }

    [Fact]
    public async Task TheMergedUserConfigIsWrittenWholeWithNoTemporaryFileLeftBesideItAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:sonnet"}}""");
        string answers = WriteAnswers(CompleteAnswers());

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(["config.json"], Directory.GetFileSystemEntries(Path.GetDirectoryName(cli.UserConfigPath)!).Select(Path.GetFileName));
        Assert.EndsWith("\n", File.ReadAllText(cli.UserConfigPath), StringComparison.Ordinal);
        Assert.Equal(aliases.Order(StringComparer.Ordinal), KeysOf(ReadUserConfig().GetProperty("models")).Order(StringComparer.Ordinal));
    }

    // The write goes to a temp file and a rename: when it cannot be made, the user's file is the old one, whole, and nothing is left beside it.
    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AWriteThatFailsLeavesTheExistingUserConfigByteIdenticalAndNoTemporaryFileAsync()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            Assert.Skip("needs a read-only directory the process cannot write into (POSIX, not root).");

        string path = cli.WriteUserConfig(/*lang=json,strict*/ """{"$schema":"keep","models":{"fast":"claude:sonnet"}}""");
        string before = File.ReadAllText(path);
        string directory = Path.GetDirectoryName(path)!;
        string answers = WriteAnswers(CompleteAnswers());
        UnixFileMode original = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            CliResult result = await InitAsync("--answers", answers);

            Assert.NotEqual(ExitCodes.Ok, result.ExitCode);
            Assert.Contains(path, result.Stderr, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(["config.json"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
        }
        finally
        {
            File.SetUnixFileMode(directory, original);
        }
    }

    // Regression (review 2, 2026-10-15): the agents were rendered with the config loaded BEFORE the answers were written, so a
    // fresh machine got the built-in model in `.claude/agents/` whatever it had just answered.
    [Fact]
    public async Task TheAgentsSyncedByAnAnswersRunUseTheAnsweredModelNotTheBuiltInAsync()
    {
        string answers = WriteAnswers(CompleteAnswers(("frontier-coding", "claude:sonnet")));

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string builder = Lf(File.ReadAllText(Path.Combine(cli.Cwd, ".claude", "agents", "builder.md")));
        Assert.Contains("\nmodel: sonnet\n", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("\nmodel: opus\n", builder, StringComparison.Ordinal);
    }

    // Regression (review 2): a partial answer set exited 0 with the repo scaffolded and synced against built-ins.
    [Theory]
    [InlineData(/*lang=json,strict*/ """{"fast":"claude:haiku","cheap-coding":"claude:haiku"}""", 3)]
    [InlineData(/*lang=json,strict*/ """{}""", 5)]
    public async Task APartialOrEmptyAnswerSetIsExitTwoCountingWhatIsLeftAndTouchesNothingInTheRepoAsync(string json, int missing)
    {
        string answers = WriteAnswers(json);
        string[] before = RepoEntries();

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal([$"model aliases: {missing} missing from {cli.UserConfigPath}; answer `claustrum init questions --json` and run `claustrum init --answers <file>`"], MissingFrom(result.Stderr));
        Assert.Equal(before, RepoEntries());
        foreach (string created in new[] { ".claustrum", ".claude", ".gitignore", "AGENTS.md", "claustrum.json" })
            Assert.False(Path.Exists(Path.Combine(cli.Cwd, created)), $"{created} must not exist after a refused init");
    }

    // The answers that were given are kept, so the next run only has the remainder to ask for.
    [Fact]
    public async Task TheAnswersOfAPartialSetAreStillWrittenAndOnlyTheRestIsAskedNextAsync()
    {
        string answers = WriteAnswers(/*lang=json,strict*/ """{"fast":"claude:haiku","cheap-coding":"claude:haiku"}""");

        await InitAsync("--answers", answers);

        (string[] keys, _) = await QuestionsAsync();
        Assert.Equal(["frontier-coding", "frontier-reasoning", "standard-coding"], keys.Order(StringComparer.Ordinal));
    }

    // Regression (review 2): an alias a repo claustrum.json defines wins at run time, so it is not asked about and not missing.
    [Fact]
    public async Task AnAliasARepoClaustrumJsonDefinesIsNotAskedAboutNorCountedMissingAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:vendor/coder","fast":"claude:haiku"}}""");

        (string[] keys, _) = await QuestionsAsync();

        Assert.Equal(["cheap-coding", "frontier-reasoning", "standard-coding"], keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnAnswerSetThatCoversOnlyWhatTheRepoLeavesUndefinedIsExitZeroAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:vendor/coder","fast":"claude:haiku"}}""");
        string answers = WriteAnswers(/*lang=json,strict*/ """{"frontier-reasoning":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku"}""");

        CliResult result = await InitAsync("--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal(["cheap-coding", "frontier-reasoning", "standard-coding"], KeysOf(ReadUserConfig().GetProperty("models")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WithTheRepoDefiningSomeAliasesTheRedirectedNoAnswersRunCountsOnlyTheUndefinedOnesAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{"frontier-coding":"opencode:vendor/coder","fast":"claude:haiku"}}""");

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal([$"model aliases: 3 missing from {cli.UserConfigPath}; answer `claustrum init questions --json` and run `claustrum init --answers <file>`"], MissingFrom(result.Stderr));
    }

    // A repo that defines every alias leaves nothing to answer: init runs without a user config's help.
    [Fact]
    public async Task ARepoClaustrumJsonThatDefinesEveryAliasLeavesNothingMissingAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"models":{"frontier-reasoning":"claude:opus","frontier-coding":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"}}""");

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        (string[] keys, _) = await QuestionsAsync();
        Assert.Empty(keys);
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
