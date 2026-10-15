using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #121 through the built binary: `cast questions` shows what every alias resolves to and which layer said so; `cast create` and
// `cast new` print the same alias → target line per role on stdout, put a warning on stderr for each built-in fallback, and store
// what each model resolved to (`resolved`) so `run` can warn later if the config moves it. PATH is emptied: no backend is reached.
public sealed class CastModelsEndToEndTests : IDisposable
{
    private const string BuiltinNote = "built-in default — no claustrum.json or user config defines it";

    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public CastModelsEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> RunAsync(string[] args, string stdin = "") => cli.RunAsync(args, stdin, emptyPath);

    private async Task<CliResult> CreateAsync(string answersJson)
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), answersJson);
        return await RunAsync(["cast", "create", "--answers", "answers.json"]);
    }

    private JsonElement ReadDefaultCast()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(cli.CastsDirectory, "default.json")));
        return document.RootElement.Clone();
    }

    private static JsonElement Model(JsonElement questions, string alias) =>
        questions.GetProperty("models").EnumerateArray().Single(row => row.GetProperty("alias").GetString() == alias);

    // ---- cast questions ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task QuestionsJsonCarriesAModelsRowPerAliasNamingTargetLayerAndSourceForAllThreeLayersAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"standard-coding":"opencode:vendor/user-model","only-in-user":"claude:opus"}}""");
        cli.MarkAsGitRoot();
        string repoConfig = Path.Combine(cli.Cwd, "claustrum.json");
        File.WriteAllText(repoConfig, /*lang=json,strict*/ """{"models":{"fast":"claude:from-repo"}}""");

        CliResult result = await RunAsync(["cast", "questions", "--json"]);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;

        JsonElement builtin = Model(root, "cheap-coding");
        Assert.Equal("claude:haiku", builtin.GetProperty("target").GetString());
        Assert.Equal("builtin", builtin.GetProperty("layer").GetString());
        Assert.Equal("built-in default", builtin.GetProperty("source").GetString());

        JsonElement user = Model(root, "only-in-user");
        Assert.Equal("claude:opus", user.GetProperty("target").GetString());
        Assert.Equal("user", user.GetProperty("layer").GetString());
        Assert.Equal(cli.UserConfigPath, user.GetProperty("source").GetString());

        JsonElement repo = Model(root, "fast");
        Assert.Equal("claude:from-repo", repo.GetProperty("target").GetString());
        Assert.Equal("repo", repo.GetProperty("layer").GetString());
        Assert.EndsWith("claustrum.json", repo.GetProperty("source").GetString(), StringComparison.Ordinal);

        Assert.Equal("user", Model(root, "standard-coding").GetProperty("layer").GetString());
        string[] aliases = [.. root.GetProperty("models").EnumerateArray().Select(row => row.GetProperty("alias").GetString()!)];
        Assert.Equal(aliases.Order(StringComparer.Ordinal), aliases);
    }

    [Fact]
    public async Task QuestionsTextPrintsTheModelsTableBeforeTheQuestionsAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"fast":"claude:sonnet"}}""");

        CliResult result = await RunAsync(["cast", "questions"]);

        string[] lines = Lf(result.Stdout).Split('\n');
        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        int table = Array.IndexOf(lines, "models:");
        Assert.True(table >= 0, result.Stdout);
        Assert.Contains($"  fast → claude:sonnet ({cli.UserConfigPath})", lines);
        Assert.Contains($"  cheap-coding → claude:haiku ({BuiltinNote})", lines);
        Assert.True(table < Array.FindIndex(lines, line => line.StartsWith("architect:", StringComparison.Ordinal)), "the table comes first");
    }

    // ---- cast create / cast new ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CreatePrintsOneModelLinePerRoleAndWarnsOnStderrForEachBuiltInFallbackAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"cheap-coding":"claude:sonnet"}}""");

        CliResult result = await CreateAsync(/*lang=json,strict*/ """{"architect":"host","builder":"cheap-coding","code-reviewer":"fast","tester":"claude:opus"}""");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string[] stdout = Lf(result.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Path.Combine(cli.CastsDirectory, "default.json"), stdout[0]);
        Assert.Contains($"cheap-coding → claude:sonnet ({cli.UserConfigPath})", stdout);
        Assert.Contains($"fast → claude:haiku ({BuiltinNote})", stdout);
        Assert.Contains("claude:opus → claude:opus (free-form, as typed)", stdout);
        Assert.Equal(4, stdout.Length);

        string[] warnings = Lf(result.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [$"warning: cast 'default' role 'code-reviewer': alias 'fast' resolved to the built-in default claude:haiku; no claustrum.json or user config defines it (looked in {cli.UserConfigPath})"],
            warnings);
    }

    [Fact]
    public async Task CreateStoresWhatEachModelResolvedToOnTheRoleEntriesAndTheArchitectAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"cheap-coding":"claude:sonnet"}}""");

        await CreateAsync(/*lang=json,strict*/ """{"architect":"spawned on frontier-reasoning","builder":"cheap-coding","code-reviewer":"fast","tester":"claude:opus"}""");

        JsonElement cast = ReadDefaultCast();
        JsonElement roles = cast.GetProperty("roles");
        Assert.Equal("cheap-coding", roles.GetProperty("builder").GetProperty("model").GetString());
        Assert.Equal("claude:sonnet", roles.GetProperty("builder").GetProperty("resolved").GetString());
        Assert.Equal("claude:haiku", roles.GetProperty("code-reviewer").GetProperty("resolved").GetString());
        Assert.Equal("claude:opus", roles.GetProperty("tester").GetProperty("resolved").GetString());
        Assert.Equal("frontier-reasoning", cast.GetProperty("architect").GetProperty("model").GetString());
        Assert.Equal("claude:opus", cast.GetProperty("architect").GetProperty("resolved").GetString());
    }

    [Fact]
    public async Task CreateWithEveryAliasDefinedWarnsOfNothingAsync()
    {
        cli.WriteUserConfig();

        CliResult result = await CreateAsync(/*lang=json,strict*/ """{"architect":"host","builder":"cheap-coding","code-reviewer":"frontier-coding"}""");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task CreateWithAUserAliasChainedToABuiltInWarnsNamingTheBuiltInHopAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"mine":"fast"}}""");

        CliResult result = await CreateAsync(/*lang=json,strict*/ """{"architect":"host","builder":"mine"}""");

        Assert.Equal(
            [$"warning: cast 'default' role 'builder': alias 'mine' ({cli.UserConfigPath}) chains to the built-in default 'fast' = claude:haiku"],
            Lf(result.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task CastNewPrintsTheSameLinesAndWarningsAndStoresResolvedAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"models":{"cheap-coding":"claude:sonnet"}}""");

        // architect, builder, code-reviewer, demo-author, tester, ui-reviewer, builder_max_parallel, budget
        CliResult result = await RunAsync(["cast", "new"], "host\nfast\nnot needed\nnot needed\ncheap-coding\nclaude:sonnet\n2\n10\n");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string stdout = Lf(result.Stdout);
        Assert.Contains($"fast → claude:haiku ({BuiltinNote})\n", stdout, StringComparison.Ordinal);
        Assert.Contains($"cheap-coding → claude:sonnet ({cli.UserConfigPath})\n", stdout, StringComparison.Ordinal);
        Assert.Contains($"warning: cast 'default' role 'builder': alias 'fast' resolved to the built-in default claude:haiku; no claustrum.json or user config defines it (looked in {cli.UserConfigPath})", Lf(result.Stderr), StringComparison.Ordinal);
        Assert.Equal("claude:haiku", ReadDefaultCast().GetProperty("roles").GetProperty("builder").GetProperty("resolved").GetString());
        Assert.Equal("claude:sonnet", ReadDefaultCast().GetProperty("roles").GetProperty("tester").GetProperty("resolved").GetString());
    }
}
