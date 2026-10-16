using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #126 where the retry is not a plain `run`: a child hosted detached in a tree whose architect is live (the host retries, the waiting `run` follows
// `retried_as` and prints the retry's receipt), and `coordinate`'s spawned architect (retried as a whole new coordinate job, so the printout, the tree and
// the branch are the retry's). The same fake opencode that is refused, the same fake claude as the fallback; DetachedRepo kills every host.
public sealed class FallbackRetryHostedEndToEndTests : IDisposable
{
    private readonly DetachedRepo fixture = new();

    public void Dispose() => fixture.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;

    private static JsonElement OneDocument(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    // Runner writes a receipt the first time non-atomically (LiveJob.ReadResult says so), so a reader may see it empty before it is ever valid. #126's
    // rewrite with retried_as goes through AtomicFile: after a valid version has been seen, an unparseable one is a torn rewrite.
    private Task<string[]> WatchForReceiptTornAfterItWasValidAsync(CancellationToken stop) => Task.Run(
        () =>
        {
            HashSet<string> valid = [];
            List<string> torn = [];
            while (!stop.IsCancellationRequested)
            {
                foreach (string id in fixture.JobIds())
                {
                    try
                    {
                        using FileStream stream = new(Path.Combine(fixture.JobDir(id), "result.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using StreamReader reader = new(stream);
                        string text = reader.ReadToEnd();
                        try
                        {
                            using JsonDocument document = JsonDocument.Parse(text);
                            valid.Add(id);
                        }
                        catch (JsonException) when (valid.Contains(id))
                        {
                            torn.Add($"{id}/result.json: '{text}'");
                        }
                        catch (JsonException)
                        {
                            // Not yet written whole, and never seen valid: the first, non-atomic write.
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // No receipt yet.
                    }
                }
            }

            return torn.ToArray();
        },
        CancellationToken.None);

    [Fact]
    public async Task AChildHostedInATreeIsRetriedByItsHostAndTheWaitingRunPrintsTheRetrysReceiptAsync()
    {
        FakeRefusals.Opencode opencode = FallbackSetup.Apply(fixture.Repo, FakeRefusals.RealQuotaEvents());
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("ran on the fallback")] });
        Dictionary<string, string?> tree = DetachedRepo.Tree(fixture.MintRoot());
        using CancellationTokenSource stop = new();
        Task<string[]> watcher = WatchForReceiptTornAfterItWasValidAsync(stop.Token);

        CliResult process = await fixture.RunBuilderAsync(tree, "--json");
        await stop.CancelAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        JsonElement printed = OneDocument(process.Stdout);
        Assert.Equal("success", Str(printed, "status"));
        Assert.Equal("claude", Str(printed, "backend"));
        string retryId = Str(printed, "job_id");
        string firstId = Assert.Single(fixture.JobIds(), id => id != retryId && fixture.Json(id, "host.json") is not null);
        Assert.Equal(firstId, Str(printed, "fallback_of"));
        Assert.Contains(printed.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()), text => text!.Contains("failed before starting (provider_quota:", StringComparison.Ordinal));

        JsonElement first = fixture.Json(firstId, "result.json") ?? throw new InvalidOperationException("no receipt");
        Assert.Equal("failed", Str(first, "status"));
        Assert.Equal(retryId, Str(first, "retried_as"));
        Assert.Equal(1, opencode.Runs);

        // The first receipt was rewritten with retried_as while a reader polled it: once it had been valid, no version of it was ever torn.
        Assert.Empty(await watcher);
    }

    [Fact]
    public async Task ATreeChildThatCannotBeRetriedStillPrintsItsOwnReceiptAfterTheRetryWindowAsync()
    {
        FallbackSetup.Apply(fixture.Repo, FakeRefusals.OpencodeEvents(/*lang=json,strict*/ """{"type":"unknown","message":"Agent not found"}"""));
        fixture.Repo.Script(new FakeClaudeScript { Turns = [FakeClaudeTurn.Report("ran on the fallback")] });

        CliResult process = await fixture.RunBuilderAsync(DetachedRepo.Tree(fixture.MintRoot()), "--json");

        Assert.NotEqual(ExitCodes.Ok, process.ExitCode);
        JsonElement printed = OneDocument(process.Stdout);
        Assert.Equal("failed", Str(printed, "status"));
        Assert.Equal("other", Str(printed, "error_kind"));
        Assert.False(Has(printed, "retried_as"));
        Assert.Single(fixture.JobIds(), id => fixture.Json(id, "host.json") is not null);
    }
}

public sealed class FallbackRetryCoordinateEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    [Fact]
    public async Task ASpawnedArchitectRefusedByItsProviderIsRetriedAsAWholeCoordinateJobAndTheRetrysIsThePrintoutAsync()
    {
        FakeRefusals.Opencode opencode = FallbackSetup.Apply(repo, FakeRefusals.RealQuotaEvents(), spawnedArchitect: true);
        repo.Script(new FakeClaudeScript { Commands = ["echo done > retried-architect.txt"], Summary = "integrated" });
        repo.CommitAll("fallback cast");

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string retryId = Str(result, "job_id");
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal("claude", Str(result, "backend"));
        string firstId = Assert.Single(repo.JobDirectories(), id => id != retryId && File.Exists(Path.Combine(repo.JobsRoot, id, "result.json")));
        Assert.Equal(firstId, Str(result, "fallback_of"));
        Assert.Equal(1, opencode.Runs);

        // The printout, the branch and the architect's work are the retry's; the first architect's receipt only points at it.
        Assert.Equal($"claustrum/{retryId}", Str(result, "branch"));
        Assert.Contains("retried-architect.txt", repo.Git("ls-tree", "-r", "--name-only", $"claustrum/{retryId}"), StringComparison.Ordinal);
        JsonElement first = repo.ReadResult(firstId);
        Assert.Equal("failed", Str(first, "status"));
        Assert.Equal("provider_quota", Str(first, "error_kind"));
        Assert.Equal(retryId, Str(first, "retried_as"));
        Assert.Contains(result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()), text => text!.Contains("architect: opencode:fake/pro failed before starting (provider_quota:", StringComparison.Ordinal));
    }
}
