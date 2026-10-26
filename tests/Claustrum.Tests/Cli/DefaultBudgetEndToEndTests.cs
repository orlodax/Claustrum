using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #137 through the built binary: `defaults.budget_usd: null` in the user config turns the default cap off, `backends doctor` says so with
// the layer that said it, `init --answers` rewrites the user config without dropping the null, and a budget that is neither number nor null
// is exit 2 naming the file. PATH is emptied and CLAUSTRUM_BUDGET_USD set or removed on the child explicitly, so the developer's own
// export cannot decide the answer.
public sealed class DefaultBudgetEndToEndTests : IDisposable
{
    private const string UserNull = /*lang=json,strict*/ """
        {
          "models": {"frontier-reasoning":"claude:opus","frontier-coding":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"},
          "defaults": {"budget_usd": null}
        }
        """;

    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public DefaultBudgetEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private Task<CliResult> RunAsync(string? envBudget, params string[] args) =>
        cli.RunAsync(args, "", emptyPath, new Dictionary<string, string?> { ["CLAUSTRUM_BUDGET_USD"] = envBudget });

    [Fact]
    public async Task DoctorPrintsNoneWithTheUserLayerForANullInTheUserConfigAsync()
    {
        cli.WriteUserConfig(UserNull);

        CliResult result = await RunAsync(null, "backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  defaults.budget_usd = none  [User]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorStillPrintsTheNumberAndItsLayerForANumberedDefaultAsync()
    {
        cli.WriteUserConfig(UserNull.Replace("null", "7.5", StringComparison.Ordinal));

        CliResult result = await RunAsync(null, "backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  defaults.budget_usd = 7.5  [User]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorAttributesANoneInTheEnvironmentToTheEnvLayerOverAFileNumberAsync()
    {
        cli.WriteUserConfig(UserNull.Replace("null", "7.5", StringComparison.Ordinal));

        CliResult result = await RunAsync("none", "backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  defaults.budget_usd = none  [Env]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANumberInTheEnvironmentBeatsAUserNullInDoctorAsync()
    {
        cli.WriteUserConfig(UserNull);

        CliResult result = await RunAsync("2", "backends", "doctor");

        Assert.Contains("  defaults.budget_usd = 2  [Env]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonNumericEnvironmentWordIsIgnoredAndTheUserNullStandsAsync()
    {
        cli.WriteUserConfig(UserNull);

        CliResult result = await RunAsync("unlimited", "backends", "doctor");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("  defaults.budget_usd = none  [User]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepoClaustrumJsonThatOmitsTheKeyLeavesTheUsersNoneStandingInDoctorAsync()
    {
        cli.WriteUserConfig(UserNull);
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), /*lang=json,strict*/ """{"defaults":{"timeout_seconds":60}}""");

        CliResult result = await RunAsync(null, "backends", "doctor");

        Assert.Contains("  defaults.budget_usd = none  [User]", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("  defaults.timeout_seconds = 60  [Repo]", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ "\"none\"")]
    [InlineData("true")]
    [InlineData(/*lang=json,strict*/ """{"usd":5}""")]
    public async Task ANonNumberNonNullBudgetInTheUserConfigIsExitTwoNamingTheFileAsync(string value)
    {
        string path = cli.WriteUserConfig(UserNull.Replace("null", value, StringComparison.Ordinal));

        CliResult result = await RunAsync(null, "backends", "doctor");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(path, result.Stderr, StringComparison.Ordinal);
    }

    // init's rewrite of the user config must keep `"budget_usd": null`: a rewrite that dropped it would bring the built-in $5 back.
    [Fact]
    public async Task InitAnswersRewritingTheUserConfigKeepsANullBudgetAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"defaults":{"budget_usd":null,"timeout_seconds":90}}""");
        string answers = Path.Combine(cli.Cwd, "answers.json");
        File.WriteAllText(answers, /*lang=json,strict*/ """{"frontier-reasoning":"claude:opus","frontier-coding":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"}""");

        CliResult result = await RunAsync(null, "init", "--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(cli.UserConfigPath));
        JsonElement defaults = document.RootElement.GetProperty("defaults");
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("budget_usd").ValueKind);
        Assert.Equal(90, defaults.GetProperty("timeout_seconds").GetInt32());
        Assert.Equal("claude:opus", document.RootElement.GetProperty("models").GetProperty("frontier-reasoning").GetString());
    }

    [Fact]
    public async Task InitAnswersRewritingAUserConfigWithNoBudgetKeyDoesNotInventOneAsync()
    {
        cli.WriteUserConfig(/*lang=json,strict*/ """{"defaults":{"timeout_seconds":90}}""");
        string answers = Path.Combine(cli.Cwd, "answers.json");
        File.WriteAllText(answers, /*lang=json,strict*/ """{"frontier-reasoning":"claude:opus","frontier-coding":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"}""");

        CliResult result = await RunAsync(null, "init", "--answers", answers);

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(cli.UserConfigPath));
        Assert.False(document.RootElement.GetProperty("defaults").TryGetProperty("budget_usd", out _), "a silent layer must stay silent when rewritten");
    }
}
