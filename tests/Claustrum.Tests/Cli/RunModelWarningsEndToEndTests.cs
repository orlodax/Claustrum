using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #121/#122 F9 through the built binary, against a stand-in `claude`: `run` never silently uses a model that only a built-in default
// supplied, nor one whose stored `resolved` has drifted from today's config. The warning is on stderr BEFORE the backend starts
// (with --json too, where nothing else prints) and in the receipt's warnings[]; a --model or --backend flag silences the cast's
// warning; a free-form backend:model-id has nothing to fall through; a role whose model comes from its tier class warns too, even
// when the cast names no model for it.
public sealed class RunModelWarningsEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string UserPath => UserConfig.PathFor(repo.Home);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // IsolatedRepo's complete user config minus the aliases a test wants to be a built-in fallback.
    private void UserAliases(params (string Alias, string Target)[] defined)
    {
        string models = string.Join(",", defined.Select(entry => $"\"{entry.Alias}\":\"{entry.Target}\""));
        UserConfig.Write(repo.Home, "{\"models\":{" + models + "}}");
    }

    private void WriteCast(string? builderModel, string? resolved)
    {
        Directory.CreateDirectory(Path.Combine(repo.Repo, ".claustrum", "casts"));
        string model = builderModel is null ? "null" : $"\"{builderModel}\"";
        string stored = resolved is null ? "null" : $"\"{resolved}\"";
        File.WriteAllText(
            Path.Combine(repo.Repo, ".claustrum", "casts", "default.json"),
            "{\"name\":\"default\",\"library\":\"1.0.0\",\"architect\":{\"mode\":\"host\"},\"roles\":{\"builder\":{\"model\":" + model
            + ",\"backend\":null,\"tier\":null,\"max_parallel\":null,\"resolved\":" + stored + "}},\"budget_usd\":null}");
    }

    private static string[] Warnings(string stderr) =>
        [.. stderr.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("warning: ", StringComparison.Ordinal)).Select(line => line["warning: ".Length..])];

    private static string[] ReceiptWarnings(JsonElement receipt) => [.. receipt.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!)];

    // The repo is a git root with a claustrum.json, so the warning says it looked there too; the child may spell the repo path its own way.
    private void AssertBuiltinWarning(string alias, string target, string actual)
    {
        Assert.StartsWith($"cast 'default' role 'builder': alias '{alias}' resolved to the built-in default {target}; no claustrum.json or user config defines it (looked in {UserPath}, ", actual, StringComparison.Ordinal);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}claustrum.json)", actual, StringComparison.Ordinal);
    }

    // ---- before the run, with --json too ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABuiltInFallbackIsWarnedOnStderrWhileTheBackendHasNotFinishedAndAgainInTheReceiptAsync()
    {
        UserAliases(("frontier-coding", "claude:opus"));
        WriteCast("cheap-coding", resolved: "claude:haiku");
        repo.Script(new FakeClaudeScript { SleepSeconds = 4 });
        using RunningCli run = repo.StartCli("run", "builder", "--brief", "do the task", "--json");

        await run.WaitUntilAsync(() => run.Stderr.Contains("warning: cast 'default'", StringComparison.Ordinal), "the pre-run warning", TimeSpan.FromSeconds(30), Ct);

        Assert.False(run.HasExited, "the warning must reach stderr before the run is over");
        Assert.Equal(ExitCodes.Ok, await run.WaitForExitAsync(TimeSpan.FromSeconds(60), Ct));
        AssertBuiltinWarning("cheap-coding", "claude:haiku", Assert.Single(Warnings(run.Stderr)));
        using JsonDocument document = JsonDocument.Parse(run.Stdout);
        AssertBuiltinWarning("cheap-coding", "claude:haiku", Assert.Single(ReceiptWarnings(document.RootElement)));
    }

    [Fact]
    public async Task AStoredResolutionThatDriftedIsWarnedOnStderrAndInTheReceiptAsync()
    {
        UserAliases(("cheap-coding", "claude:haiku"), ("frontier-coding", "claude:opus"));
        WriteCast("cheap-coding", resolved: "claude:sonnet");

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        string expected = $"cast 'default' role 'builder': alias 'cheap-coding' resolved to claude:sonnet when the cast was created, now to claude:haiku ({UserPath})";
        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal([expected], Warnings(process.Stderr));
        Assert.Equal([expected], ReceiptWarnings(result));
    }

    [Fact]
    public async Task InHumanModeEachWarningIsPrintedOnceNotOnceBeforeAndOnceAfterTheRunAsync()
    {
        UserAliases(("frontier-coding", "claude:opus"));
        WriteCast("cheap-coding", resolved: null);

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        AssertBuiltinWarning("cheap-coding", "claude:haiku", Assert.Single(Warnings(process.Stderr)));
        Assert.Contains("status: Success (exit 0)", process.Stdout, StringComparison.Ordinal);
    }

    // ---- who silences it -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--model", "claude:haiku")]
    [InlineData("--backend", "claude")]
    public async Task AModelOrBackendFlagSuppressesTheCastsWarningAsync(string flag, string value)
    {
        UserAliases(("frontier-coding", "claude:opus"));
        WriteCast("cheap-coding", resolved: "claude:sonnet");

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync(flag, value);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.DoesNotContain("cast 'default'", process.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ReceiptWarnings(result), warning => warning.Contains("cast 'default'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreeFormBackendModelIdInTheCastWarnsOfNothingAsync()
    {
        UserAliases();
        WriteCast("claude:opus", resolved: "claude:opus");

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Empty(Warnings(process.Stderr));
        Assert.Empty(ReceiptWarnings(result));
    }

    [Fact]
    public async Task ACastStoredWithoutResolvedRunsWithoutADriftWarningAsync()
    {
        UserAliases(("cheap-coding", "claude:haiku"), ("frontier-coding", "claude:opus"));
        WriteCast("cheap-coding", resolved: null);

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Empty(Warnings(process.Stderr));
        Assert.Empty(ReceiptWarnings(result));
    }

    [Fact]
    public async Task AUserAliasChainedToABuiltInWarnsNamingTheBuiltInHopOnceAsync()
    {
        UserAliases(("mine", "fast"), ("frontier-coding", "claude:opus"));
        WriteCast("mine", resolved: "claude:haiku");

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        string expected = $"cast 'default' role 'builder': alias 'mine' ({UserPath}) chains to the built-in default 'fast' = claude:haiku";
        Assert.Equal([expected], Warnings(process.Stderr));
        Assert.Equal([expected], ReceiptWarnings(result));
    }

    // ---- F9: every role whose model falls to a built-in warns, even when the cast names no model for it -------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARoleWhoseTierClassFellToABuiltInWarnsWhetherOrNotACastNamesItsModelAsync(bool withACast)
    {
        UserAliases(("cheap-coding", "claude:haiku"));
        if (withACast)
            WriteCast(builderModel: null, resolved: null);

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        string expected = $"role 'builder': model 'frontier-coding' resolved to the built-in default claude:opus; {UserPath} does not define it";
        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal([expected], Warnings(process.Stderr));
        Assert.Equal([expected], ReceiptWarnings(result));
    }

    [Fact]
    public async Task ARoleWhoseTierClassTheUserConfigDefinesWarnsOfNothingAsync()
    {
        UserAliases(("frontier-coding", "claude:opus"));

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Empty(Warnings(process.Stderr));
        Assert.Empty(ReceiptWarnings(result));
    }

    // The cast's own warning already said it for this role, so the role-level one is not a second line about the same fallback.
    [Fact]
    public async Task TheCastsWarningAndTheRoleLevelOneAreNotBothPrintedForTheSameRoleAsync()
    {
        UserAliases();
        WriteCast("cheap-coding", resolved: "claude:haiku");

        (CliResult process, _) = await repo.RunBuilderAsync();

        string[] warnings = Warnings(process.Stderr);
        Assert.Single(warnings);
        Assert.StartsWith("cast 'default' role 'builder': ", warnings[0], StringComparison.Ordinal);
    }
}
