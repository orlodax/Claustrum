using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #122 through the built binary: with neither a user config nor a repo claustrum.json, every command that needs configuration
// (run, coordinate, cast questions/new/create) exits 2 before it does anything, naming both paths and `claustrum init`; the commands
// that do not need it stay as they were; a user config alone - with no cast - or a repo claustrum.json alone is enough.
// The child's HOME and APPDATA are the scratch home, so the developer's own config can never be the one that is found.
public sealed class UninitializedMachineEndToEndTests : IDisposable
{
    private readonly ClaustrumCli cli = new();
    private readonly string emptyPath;

    public UninitializedMachineEndToEndTests() => emptyPath = Directory.CreateDirectory(Path.Combine(cli.Home, "empty-path")).FullName;

    public void Dispose() => cli.Dispose();

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> RunAsync(params string[] args) => cli.RunAsync(args, "", emptyPath);

    public static TheoryData<string> GatedCommands =>
    [
        "run builder --brief x",
        "run builder --brief x --json",
        "coordinate --brief x",
        "coordinate --brief x --json",
        "cast questions",
        "cast questions --json",
        "cast new",
        "cast create --answers answers.json",
    ];

    private void AssertRefused(CliResult result, string repoConfigText)
    {
        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal($"this repo isn't initialized: no user config at {cli.UserConfigPath} and no claustrum.json at {repoConfigText}; run `claustrum init`", Lf(result.Stderr).Trim());
        Assert.Equal("", result.Stdout);
        Assert.False(Directory.Exists(cli.JobsDirectory), "no job directory is minted for a refused command");
        Assert.False(Directory.Exists(Path.Combine(cli.Cwd, ".claustrum")), "nothing is created in the repo");
    }

    [Theory]
    [MemberData(nameof(GatedCommands))]
    public async Task EachGatedCommandExitsTwoNamingBothPathsAndTheFixAsync(string commandLine)
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus"}""");

        CliResult result = await RunAsync(commandLine.Split(' '));

        AssertRefused(result, "<no git root>");
    }

    [Theory]
    [MemberData(nameof(GatedCommands))]
    public async Task InAGitRepositoryTheRefusalNamesThePathTheRepoConfigWouldHaveAsync(string commandLine)
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus"}""");

        CliResult result = await RunAsync(commandLine.Split(' '));

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string stderr = Lf(result.Stderr).Trim();
        Assert.StartsWith($"this repo isn't initialized: no user config at {cli.UserConfigPath} and no claustrum.json at ", stderr, StringComparison.Ordinal);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}claustrum.json; run `claustrum init`", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("<no git root>", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cli.JobsDirectory));
    }

    public static TheoryData<string> UngatedCommands =>
    [
        "sync --dry-run",
        "roles list",
        "roles show builder",
        "backends list",
        "jobs list",
        "cast list",
        "init questions",
        "init questions --json",
    ];

    [Theory]
    [MemberData(nameof(UngatedCommands))]
    public async Task TheCommandsThatNeedNoConfigurationStayUngatedAsync(string commandLine)
    {
        CliResult result = await RunAsync(commandLine.Split(' '));

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.DoesNotContain("this repo isn't initialized: no user config", result.Stderr + result.Stdout, StringComparison.Ordinal);
    }

    // ---- what counts as initialized ----------------------------------------------------------------------------------

    [Fact]
    public async Task AUserConfigWithNoCastIsNotRefusedAndCastQuestionsRunsAsync()
    {
        cli.WriteUserConfig("{}");

        CliResult result = await RunAsync("cast", "questions", "--json");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert.True(document.RootElement.TryGetProperty("questions", out _));
    }

    [Fact]
    public async Task AUserConfigWithNoCastLetsRunPastTheGateToTheBackendLookupAsync()
    {
        cli.WriteUserConfig("{}");

        CliResult result = await RunAsync("run", "builder", "--brief", "x", "--backend", "nonexistent", "--json");

        Assert.DoesNotContain("isn't initialized", result.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("backend_missing", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AUserConfigWithNoCastLetsCoordinateReachItsOwnNoCastRefusalAsync()
    {
        cli.WriteUserConfig("{}");

        CliResult result = await RunAsync("coordinate", "--brief", "x");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.DoesNotContain("isn't initialized", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("cast", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARepoClaustrumJsonAloneCountsAsInitializedAsync()
    {
        cli.MarkAsGitRoot();
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), "{}");

        CliResult questions = await RunAsync("cast", "questions", "--json");
        CliResult run = await RunAsync("run", "builder", "--brief", "x", "--backend", "nonexistent", "--json");

        Assert.Equal(ExitCodes.Ok, questions.ExitCode);
        Assert.DoesNotContain("isn't initialized", questions.Stderr + run.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        Assert.Equal("backend_missing", document.RootElement.GetProperty("status").GetString());
    }

    // A claustrum.json in a directory that is no git repository is never read (Config.Load looks only at the git root).
    [Fact]
    public async Task AClaustrumJsonOutsideAnyGitRepositoryDoesNotCountAsync()
    {
        File.WriteAllText(Path.Combine(cli.Cwd, "claustrum.json"), "{}");

        CliResult result = await RunAsync("cast", "questions", "--json");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains("this repo isn't initialized", result.Stderr, StringComparison.Ordinal);
    }

    // Init is how the machine gets out of this state, so init cannot be behind the gate: with its answers it is initialized.
    [Fact]
    public async Task InitWithAnswersEndsTheRefusalAsync()
    {
        string answers = Path.Combine(cli.Cwd, "answers.json");
        File.WriteAllText(answers, /*lang=json,strict*/ """{"frontier-reasoning":"claude:opus","frontier-coding":"claude:opus","standard-coding":"claude:sonnet","cheap-coding":"claude:haiku","fast":"claude:haiku"}""");

        CliResult init = await RunAsync("init", "--answers", answers);
        CliResult questions = await RunAsync("cast", "questions", "--json");

        Assert.Equal(ExitCodes.Ok, init.ExitCode);
        Assert.True(File.Exists(cli.UserConfigPath));
        Assert.Equal(ExitCodes.Ok, questions.ExitCode);
    }
}
