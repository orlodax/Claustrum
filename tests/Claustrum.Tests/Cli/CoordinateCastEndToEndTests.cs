using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #122/#121 `coordinate` through the built binary, with a stand-in `claude`: the cast is per-developer and git-ignored, so an untracked
// cast is fine when a committed rule ignores `.claustrum/casts/` - and the architect's worktree, cut from HEAD, is handed a copy of it
// that its own `git add -A` never commits - while the architect's model warnings reach the receipt like a `run`'s do (F9: the
// architect, whose model falls to a built-in even when the cast names none, warns too).
public sealed class CoordinateCastEndToEndTests : IDisposable
{
    // IsolatedRepo's own .gitignore: the whole of `.claustrum/`, which is a rule ignoring `.claustrum/casts/`.
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private string UserPath => UserConfig.PathFor(repo.Home);

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private void WriteArchitectCast(string? model, string? resolved)
    {
        Directory.CreateDirectory(Path.Combine(repo.Repo, ".claustrum", "casts"));
        string modelText = model is null ? "null" : $"\"{model}\"";
        string resolvedText = resolved is null ? "null" : $"\"{resolved}\"";
        File.WriteAllText(
            Path.Combine(repo.Repo, ".claustrum", "casts", "default.json"),
            "{\"name\":\"default\",\"library\":\"1.0.0\",\"architect\":{\"mode\":\"spawned\",\"model\":" + modelText + ",\"tier\":null,\"resolved\":" + resolvedText
            + "},\"roles\":{\"builder\":{\"backend\":\"claude\"}},\"budget_usd\":null}");
    }

    private static string[] ReceiptWarnings(JsonElement receipt) => [.. receipt.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!)];

    private static string[] StderrWarnings(string stderr) =>
        [.. stderr.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("warning: ", StringComparison.Ordinal)).Select(line => line["warning: ".Length..])];

    // ---- the untracked, ignored cast -----------------------------------------------------------------------------------------

    // Regression F1 (2026-10-15): the polarity of the check was inverted, so a cast WITH the committed rule was refused.
    [Fact]
    public async Task AnUntrackedCastWithACommittedCastsRuleIsAcceptedAndTheArchitectsWorktreeIsHandedACopyAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCoordinateCast();
        Assert.Equal("", repo.Git("ls-files", ".claustrum/casts"));
        repo.Script(new FakeClaudeScript { Commands = ["cp .claustrum/casts/default.json cast-seen.json"], Summary = "integrated" });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string branch = $"claustrum/{Str(result, "job_id")}";
        Assert.Equal(branch, Str(result, "branch"));
        using JsonDocument copied = JsonDocument.Parse(repo.Git("show", $"{branch}:cast-seen.json"));
        Assert.Equal("spawned", copied.RootElement.GetProperty("architect").GetProperty("mode").GetString());
        Assert.DoesNotContain(".claustrum/casts/default.json", repo.Git("ls-tree", "-r", "--name-only", branch).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        Assert.Equal("", repo.Git("ls-files", ".claustrum/casts"));
        Assert.True(File.Exists(Path.Combine(repo.Repo, ".claustrum", "casts", "default.json")), "the operator's own cast is untouched");
    }

    [Fact]
    public async Task AnUntrackedCastWithNoCastsRuleIsRefusedNamingInitAndMintsNoJobAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, ".gitignore"), IsolatedRepo.MachineryIgnoreRules);
        repo.CommitAll("old rules");
        repo.WriteCoordinateCast();

        (CliResult process, _) = await repo.CoordinateAsync();

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("the cast 'default' is untracked, but no committed .gitignore rule ignores .claustrum/casts/; run `claustrum init` and commit .gitignore first", process.Stderr, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
    }

    // ---- the architect's model warnings reach the receipt ----------------------------------------------------------------------

    [Fact]
    public async Task TheArchitectsBuiltInFallbackIsWarnedBeforeTheRunAndInTheReceiptAsync()
    {
        FakeClaude.RequirePosixShell();
        UserConfig.Write(repo.Home, /*lang=json,strict*/ """{"models":{"frontier-coding":"claude:opus"}}""");
        WriteArchitectCast("frontier-reasoning", resolved: "claude:opus");

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string[] stderr = StderrWarnings(process.Stderr);
        string[] receipt = ReceiptWarnings(parsed ?? throw new InvalidOperationException(process.Stdout));
        Assert.StartsWith($"cast 'default' role 'architect': alias 'frontier-reasoning' resolved to the built-in default claude:opus; no claustrum.json or user config defines it (looked in {UserPath}, ", Assert.Single(stderr), StringComparison.Ordinal);
        Assert.Equal(stderr, receipt);
    }

    [Fact]
    public async Task AnArchitectModelThatDriftedFromWhatTheCastStoredIsWarnedInTheReceiptAsync()
    {
        FakeClaude.RequirePosixShell();
        UserConfig.Write(repo.Home, UserConfig.Complete.Replace("\"frontier-reasoning\": \"claude:opus\"", "\"frontier-reasoning\": \"claude:sonnet\"", StringComparison.Ordinal));
        WriteArchitectCast("frontier-reasoning", resolved: "claude:opus");

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string expected = $"cast 'default' role 'architect': alias 'frontier-reasoning' resolved to claude:opus when the cast was created, now to claude:sonnet ({UserPath})";
        Assert.Equal([expected], ReceiptWarnings(parsed ?? throw new InvalidOperationException(process.Stdout)));
        Assert.Equal([expected], StderrWarnings(process.Stderr));
    }

    // Regression F9: the cast names no model for the architect, so no cast warning could fire - but the model still came from a built-in.
    [Fact]
    public async Task AnArchitectWithNoModelInTheCastStillWarnsThatItsTierClassFellToABuiltInAsync()
    {
        FakeClaude.RequirePosixShell();
        UserConfig.Write(repo.Home, /*lang=json,strict*/ """{"models":{"frontier-coding":"claude:opus"}}""");
        WriteArchitectCast(model: null, resolved: null);

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string expected = $"role 'architect': model 'frontier-reasoning' resolved to the built-in default claude:opus; {UserPath} does not define it";
        Assert.Equal([expected], ReceiptWarnings(parsed ?? throw new InvalidOperationException(process.Stdout)));
        Assert.Equal([expected], StderrWarnings(process.Stderr));
    }

    [Fact]
    public async Task AnArchitectWithEverythingDefinedWarnsOfNothingAsync()
    {
        FakeClaude.RequirePosixShell();
        WriteArchitectCast("frontier-reasoning", resolved: "claude:opus");

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Empty(ReceiptWarnings(parsed ?? throw new InvalidOperationException(process.Stdout)));
        Assert.Empty(StderrWarnings(process.Stderr));
    }
}
