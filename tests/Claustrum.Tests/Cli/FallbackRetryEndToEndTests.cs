using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #126 through the built binary: a cast role on a fake opencode whose provider refuses it (the real 402 of job 20261015-164125-2f5f1b1d, or a variation),
// with the repo's fake claude as the cast's fallback. A refusal before any work is retried once, as a new job, and says so; every other failure, and every
// run the human or a session chose the model of, is left as it was. Nothing paid: both backends are scripts and the child's PATH holds neither.
public sealed class FallbackRetryEndToEndTests : IDisposable
{
    private const string Brief = "Review the diff of the branch.\n\n## Diff\n+ added a line\n";

    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static string[] Warnings(JsonElement receipt) => [.. receipt.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!)];

    private FakeRefusals.Opencode Refused(string? events = null, string? fallback = FallbackSetup.Fallback, decimal? budgetUsd = null, decimal costUsd = 0m)
    {
        FakeRefusals.Opencode opencode = FallbackSetup.Apply(repo, events ?? FakeRefusals.RealQuotaEvents(), fallback, budgetUsd, costUsd: costUsd);
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback", ArgvLog = repo.ArgvLog, StdinLog = repo.StdinLog });
        return opencode;
    }

    private async Task<(CliResult Process, JsonElement Receipt)> RunAsync(string role = "builder", params string[] extra)
    {
        CliResult process = await repo.RunAsync(["run", role, "--brief", Brief, "--json", .. extra]);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    // The jobs under this home, the retry's id first when there is more than one.
    private string[] Jobs() => repo.JobDirectories();

    private JsonElement Request(string jobId)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "request.json")));
        return document.RootElement.Clone();
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;

    private (JsonElement First, string FirstId) TheFirstJob(string retryId)
    {
        string firstId = Assert.Single(Jobs(), id => id != retryId);
        return (repo.ReadResult(firstId), firstId);
    }

    // ---- the case: the real 402 ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheReal402IsRetriedOnceOnTheFallbackAndTheReceiptSaysWhyAsync(bool noLive)
    {
        FakeRefusals.Opencode opencode = Refused();

        (CliResult process, JsonElement retry) = await RunAsync("builder", noLive ? ["--no-live"] : []);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        Assert.Equal("success", Str(retry, "status"));
        Assert.Equal("claude", Str(retry, "backend"));
        Assert.Equal("opus", Str(retry, "model"));
        Assert.Equal(2, Jobs().Length);
        Assert.Equal(1, opencode.Runs);
        (JsonElement first, string firstId) = TheFirstJob(Str(retry, "job_id"));

        // The retry's receipt: whom it replaced, and the warning that tells the owner what happened and why.
        Assert.Equal(firstId, Str(retry, "fallback_of"));
        string warning = Assert.Single(Warnings(retry), text => text.Contains("failed before starting", StringComparison.Ordinal));
        Assert.Equal(
            "builder: opencode:fake/pro failed before starting (provider_quota: This request would exceed your available credits given your current in-flight requests. "
            + $"Retry after in-flight requests settle, or add credits.); ran on claude:opus instead (job {firstId})",
            warning);

        // The first receipt: still the failure it was, with its classification, and now the pointer to the retry.
        Assert.Equal("failed", Str(first, "status"));
        Assert.Equal("provider_quota", Str(first, "error_kind"));
        Assert.StartsWith("This request would exceed your available credits", Str(first, "error_detail"), StringComparison.Ordinal);
        Assert.True(first.GetProperty("before_any_work").GetBoolean());
        Assert.Equal(Str(retry, "job_id"), Str(first, "retried_as"));
        Assert.False(Has(first, "fallback_of"));

        // request.json: the retry names its parent, the first does not; the model the retry ran is the fallback.
        Assert.Equal(firstId, Str(Request(Str(retry, "job_id")), "fallback_of"));
        Assert.False(Has(Request(firstId), "fallback_of"));
    }

    [Fact]
    public async Task TheRetryGetsTheSameBriefAsTheFirstAndNothingIsAddedToItAsync()
    {
        FakeRefusals.Opencode opencode = Refused();

        (_, JsonElement retry) = await RunAsync("code-reviewer", "--no-live");

        (_, string firstId) = TheFirstJob(Str(retry, "job_id"));
        Assert.Equal(Str(Request(firstId), "brief"), Str(Request(Str(retry, "job_id")), "brief"));
        Assert.Equal(Brief, Str(Request(firstId), "brief"));
        Assert.Contains("added a line", opencode.Message(1), StringComparison.Ordinal);
        string seen = File.ReadAllText(repo.StdinLog);
        Assert.Contains("added a line", seen, StringComparison.Ordinal);
        Assert.Contains("Review the diff of the branch.", seen, StringComparison.Ordinal);
        Assert.Equal("code-reviewer", Str(retry, "role"));
        Assert.Equal("success", Str(retry, "status"));
    }

    [Fact]
    public async Task ABlindRolesBriefThatCarriesRationaleIsRefusedAndNothingIsRetriedAsync()
    {
        FakeRefusals.Opencode opencode = Refused();

        CliResult process = await repo.RunAsync("run", "code-reviewer", "--brief", "## Plan\nstep 1", "--no-live");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("blind role", process.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, opencode.Runs);
        Assert.Empty(Jobs());
    }

    // ---- what qualifies ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FakeRefusals.Quota402, "provider_quota")]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.quota","message":"slow down","status":429}""", "provider_quota")]
    [InlineData(FakeRefusals.Auth401, "provider_auth")]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.no-route","message":"no endpoint for this model"}""", "model_not_found")]
    [InlineData(/*lang=json,strict*/ """{"type":"provider.transport","message":"bad gateway","status":502}""", "provider_transport")]
    public async Task EveryProviderSideKindThatRefusedBeforeAnyWorkIsRetriedAsync(string errorJson, string kind)
    {
        FakeRefusals.Opencode opencode = Refused(FakeRefusals.OpencodeEvents(errorJson));

        (_, JsonElement retry) = await RunAsync("builder", "--no-live");

        Assert.Equal("success", Str(retry, "status"));
        (JsonElement first, _) = TheFirstJob(Str(retry, "job_id"));
        Assert.Equal(kind, Str(first, "error_kind"));
        Assert.Equal(1, opencode.Runs);
        Assert.Contains(Warnings(retry), text => text.Contains($"({kind}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureNoOneCouldPlaceIsOtherAndIsNotRetriedAsync()
    {
        Refused(FakeRefusals.OpencodeEvents(/*lang=json,strict*/ """{"type":"unknown","message":"Agent not found"}"""));

        (CliResult process, JsonElement receipt) = await RunAsync("builder", "--no-live");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("other", Str(receipt, "error_kind"));
        Assert.True(receipt.GetProperty("before_any_work").GetBoolean());
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.False(File.Exists(repo.StdinLog), "the fallback must not have run");
    }

    [Fact]
    public async Task ARefusalAfterACompletedToolCallIsNotBeforeAnyWorkAndIsNotRetriedAsync()
    {
        Refused(FakeRefusals.OpencodeEvents(FakeRefusals.Quota402, completedToolCall: true));

        (_, JsonElement receipt) = await RunAsync("builder", "--no-live");

        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.False(receipt.GetProperty("before_any_work").GetBoolean());
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.False(File.Exists(repo.StdinLog));
    }

    [Fact]
    public async Task ARefusalAfterTheRoleChangedAFileIsNotBeforeAnyWorkAndIsNotRetriedAsync()
    {
        FakeRefusals.Opencode opencode = Refused(FakeRefusals.OpencodeEvents(FakeRefusals.Quota402));
        File.WriteAllText(opencode.ScriptPath, File.ReadAllText(opencode.ScriptPath).Replace("cat \"$dir/events\"", "echo edited > edited-by-the-role.txt\ncat \"$dir/events\"", StringComparison.Ordinal));

        (_, JsonElement receipt) = await RunAsync("builder", "--no-live");

        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.Contains("edited-by-the-role.txt", receipt.GetProperty("changed_files").GetRawText(), StringComparison.Ordinal);
        Assert.False(receipt.GetProperty("before_any_work").GetBoolean());
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.False(File.Exists(repo.StdinLog));
    }

    [Theory]
    [InlineData("--model", FallbackSetup.Primary)]
    [InlineData("--backend", "opencode")]
    public async Task AnExplicitModelOrBackendFlagIsNeverRetriedAsync(string flag, string value)
    {
        FakeRefusals.Opencode opencode = Refused();
        string[] extra = flag == "--backend" ? ["--backend", value, "--model", "fake/pro", "--no-live"] : [flag, value, "--no-live"];

        (CliResult process, JsonElement receipt) = await RunAsync("builder", extra);

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.Equal(1, opencode.Runs);
        Assert.False(File.Exists(repo.StdinLog), "you chose that model: nothing stands in for it");
    }

    [Fact]
    public async Task ARoleWhoseCastNamesNoFallbackFailsAsItAlwaysDidAsync()
    {
        Refused(fallback: null);

        (CliResult process, JsonElement receipt) = await RunAsync("builder", "--no-live");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.Single(Jobs());
        Assert.False(Has(receipt, "retried_as"));
    }

    [Fact]
    public async Task ASessionResumedWithResumeIsNeverRetriedAsync()
    {
        FakeRefusals.Opencode opencode = Refused();

        (_, JsonElement receipt) = await RunAsync("builder", "--resume", "ses_earlier", "--no-live");

        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.Equal(1, opencode.Runs);
        Assert.False(File.Exists(repo.StdinLog));
    }

    // ---- the retry is not retried --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFallbackThatIsRefusedTooEndsAsItselfAndIsNeverRetriedAsync(bool noLive)
    {
        FakeRefusals.Opencode opencode = Refused();
        string claudeCount = FakeRefusals.WriteRefusingClaude(repo.ScriptPath, 401);

        (CliResult process, JsonElement returned) = await RunAsync("builder", noLive ? ["--no-live"] : []);

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(returned, "status"));
        Assert.Equal("claude", Str(returned, "backend"));
        Assert.Equal("provider_auth", Str(returned, "error_kind"));
        Assert.False(Has(returned, "retried_as"));
        Assert.Equal(2, Jobs().Length);
        Assert.Equal(1, opencode.Runs);
        Assert.Equal(1, FakeRefusals.RunsOf(claudeCount));
        (JsonElement first, string firstId) = TheFirstJob(Str(returned, "job_id"));
        Assert.Equal(firstId, Str(returned, "fallback_of"));
        Assert.Equal(Str(returned, "job_id"), Str(first, "retried_as"));
        Assert.Equal("provider_quota", Str(first, "error_kind"));
    }

    // ---- a fallback that cannot be used --------------------------------------------------------------------------------------

    // A hand-edited cast can name a fallback on a harness the role does not list (cast create refuses it): the retry is declined, said on the first
    // receipt, and nothing runs.
    [Fact]
    public async Task AFallbackOnAHarnessTheRoleDoesNotRunOnIsDeclinedWithAWarningOnTheFirstReceiptAsync()
    {
        FakeRefusals.Opencode opencode = Refused(fallback: "api:openrouter:some/model");

        (CliResult process, JsonElement receipt) = await RunAsync("builder", "--no-live");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal("provider_quota", Str(receipt, "error_kind"));
        Assert.False(Has(receipt, "retried_as"));
        Assert.Single(Jobs());
        Assert.Equal(1, opencode.Runs);
        Assert.False(File.Exists(repo.StdinLog));
        string warning = Assert.Single(Warnings(receipt));
        Assert.StartsWith("builder: fallback api:openrouter:some/model not used: role 'builder' runs on", warning, StringComparison.Ordinal);

        // The receipt on disk says the same: it was rewritten, not just printed.
        Assert.Equal(warning, Assert.Single(Warnings(repo.ReadResult(Jobs()[0]))));
    }

    // A backend nothing registers resolves, so the retry is minted and ends backend_missing: the receipt returned is the retry's, and it says whom it replaced.
    [Fact]
    public async Task AFallbackThatResolvesButCannotStartEndsAsItsOwnReceiptNamingTheFirstAsync()
    {
        Refused(fallback: "nonexistent:model");

        (CliResult process, JsonElement returned) = await RunAsync("builder", "--no-live");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("backend_missing", Str(returned, "status"));
        (JsonElement first, string firstId) = TheFirstJob(Str(returned, "job_id"));
        Assert.Equal(firstId, Str(returned, "fallback_of"));
        Assert.Equal(Str(returned, "job_id"), Str(first, "retried_as"));
        Assert.Equal(2, Jobs().Length);
    }

    // ---- a continuation is not retried ---------------------------------------------------------------------------------------

    // `jobs answer` resumes the session the first job left, which only its own backend can read: the continuation runs pinned on opencode, is refused
    // there, and ends as that. The fallback never gets it.
    [Fact]
    public async Task AnAnsweredJobWhoseContinuationIsRefusedIsNotRetriedAsync()
    {
        FakeOpencode.RequirePosixShellForHarness();
        string asking = Directory.CreateDirectory(Path.Combine(repo.Home, "asking-opencode")).FullName;
        FakeOpencode fake = new(asking, [FakeOpencodeTurn.NeedsInput(/*lang=json,strict*/ """{"status":"needs_input","summary":"stopped","questions":[{"key":"db","prompt":"Which database?","options":[],"allow_free_form":true}]}""")]);
        string claude = repo.ScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(repo.Repo, "claustrum.json"), "{\"backends\":{\"claude\":{\"path\":\"" + claude + "\"}," + fake.BackendJson + "}}");
        FallbackSetup.WriteCast(repo);
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback", StdinLog = repo.StdinLog });
        (CliResult first, JsonElement asked) = await RunAsync("builder", "--no-live");
        Assert.Equal(ExitCodes.NeedsInput, first.ExitCode);
        string askedId = Str(asked, "job_id");

        FakeRefusals.Opencode refusing = FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents());
        repo.Script(new FakeClaudeScript { Summary = "ran on the fallback", StdinLog = repo.StdinLog });
        CliResult answer = await repo.RunAsync("jobs", "answer", askedId, "sqlite", "--json");

        Assert.NotEqual(ExitCodes.Ok, answer.ExitCode);
        using JsonDocument document = JsonDocument.Parse(answer.Stdout);
        JsonElement continued = document.RootElement;
        Assert.Equal("failed", Str(continued, "status"));
        Assert.Equal("provider_quota", Str(continued, "error_kind"));
        Assert.False(Has(continued, "retried_as"));
        Assert.False(Has(continued, "fallback_of"));
        Assert.Equal(2, Jobs().Length);
        Assert.Equal(1, refusing.Runs);
        Assert.False(File.Exists(repo.StdinLog), "a continuation's session only its own backend can read");
        Assert.Equal(1, fake.Runs);
    }

    // ---- budget --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstJobsCostIsChargedAndTheRetryGetsWhatIsLeftOfTheRunsBudgetAsync()
    {
        Refused(FakeRefusals.OpencodeEvents(FakeRefusals.Quota402), costUsd: 0.4m);

        (_, JsonElement retry) = await RunAsync("builder", "--budget", "1", "--no-live");

        Assert.Equal("success", Str(retry, "status"));
        (JsonElement first, _) = TheFirstJob(Str(retry, "job_id"));
        Assert.Equal(0.4m, first.GetProperty("cost_usd").GetDecimal());
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal("0.6", argv[Array.IndexOf(argv, "--max-budget-usd") + 1]);
    }

    [Fact]
    public async Task ABudgetTheFirstJobSpentIsNotRetriedAndTheFirstReceiptSaysWhyAsync()
    {
        Refused(FakeRefusals.OpencodeEvents(FakeRefusals.Quota402), costUsd: 0.5m);

        (CliResult process, JsonElement receipt) = await RunAsync("builder", "--budget", "0.5", "--no-live");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("failed", Str(receipt, "status"));
        Assert.Equal(0.5m, receipt.GetProperty("cost_usd").GetDecimal());
        Assert.Single(Jobs());
        Assert.False(File.Exists(repo.StdinLog));
        Assert.Contains(Warnings(receipt), text => text.Contains("fallback claude:opus not used: the run's budget of $0.5 went on the first job", StringComparison.Ordinal));
        Assert.False(Has(receipt, "retried_as"));
    }

    // ---- budget inside a tree ------------------------------------------------------------------------------------------------

    private static Dictionary<string, string?> InTree() => new() { ["CLAUSTRUM_PARENT_JOB"] = $"tree-{Guid.NewGuid():N}" };

    private async Task<(CliResult Process, JsonElement Receipt)> RunInTreeAsync(Dictionary<string, string?> tree)
    {
        CliResult process = await repo.RunWithEnvAsync(["run", "builder", "--brief", Brief, "--json", "--no-live"], tree);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        return (process, document.RootElement.Clone());
    }

    [Fact]
    public async Task TheTreesLedgerAdmitsTheRetryAgainstWhatTheFirstJobLeftAsync()
    {
        Refused(budgetUsd: 1m, costUsd: 0.4m);

        (CliResult process, JsonElement retry) = await RunInTreeAsync(InTree());

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        Assert.Equal("success", Str(retry, "status"));
        (JsonElement first, string firstId) = TheFirstJob(Str(retry, "job_id"));
        Assert.Equal(0.4m, first.GetProperty("cost_usd").GetDecimal());
        Assert.Equal(firstId, Str(retry, "fallback_of"));
    }

    [Fact]
    public async Task ATreeWhoseBudgetTheFirstJobSpentRefusesTheRetryAtAdmissionAsync()
    {
        Refused(budgetUsd: 1m, costUsd: 1m);

        (CliResult process, JsonElement returned) = await RunInTreeAsync(InTree());

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        Assert.Equal("budget_exceeded", Str(returned, "status"));
        (JsonElement first, string firstId) = TheFirstJob(Str(returned, "job_id"));
        Assert.Equal(firstId, Str(returned, "fallback_of"));
        Assert.Equal(Str(returned, "job_id"), Str(first, "retried_as"));
        Assert.Equal("failed", Str(first, "status"));
        Assert.False(File.Exists(repo.StdinLog), "the retry was refused before it started");
    }
}
