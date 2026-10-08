using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Jobs;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #58: a numeric `max_parallel` is a cap at every value, 1 included, through the cross-process
// RoleConcurrencyGate; isolation starts at 2 (or with --branch). Before, the gate ran only on the isolated
// path, so two `claustrum run builder` processes edited one tree at once while the architect's text promised
// "extra jobs wait for a slot". Real child processes, because the point is that the cap holds across them.
public sealed class RunCapEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SlotFile => Path.Combine(repo.Repo, ".claustrum", "locks", $"{RoleConcurrencyGate.KeyFor("default", "builder")}.0.lock");

    [Fact]
    public async Task TwoConcurrentInPlaceRunsUnderMaxParallelOneSerialiseAndNeitherIsIsolatedAsync()
    {
        repo.WriteCast(maxParallel: 1);
        repo.Script(new FakeClaudeScript { SleepSeconds = 1, MarkerLog = repo.MarkerLog });

        (CliResult Process, JsonElement Result)[] both = await Task.WhenAll(repo.RunBuilderAsync(), repo.RunBuilderAsync());

        foreach ((CliResult process, JsonElement result) in both)
        {
            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("branch").ValueKind);
        }

        Assert.Equal(["start", "end", "start", "end"], File.ReadAllLines(repo.MarkerLog));
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
        Assert.Single(TestGit.WorktreePaths(repo.Repo));
        Assert.Equal(TestGit.Head(repo.Repo), TestGit.RevParse(repo.Repo, "main"));
    }

    // The other side of the same change: a cast entry with no max_parallel is no cap and no gate, as before.
    [Fact]
    public async Task WithNoMaxParallelConcurrentRunsStillOverlapAsync()
    {
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { SleepSeconds = 3, MarkerLog = repo.MarkerLog });

        (CliResult Process, JsonElement Result)[] both = await Task.WhenAll(repo.RunBuilderAsync(), repo.RunBuilderAsync());

        foreach ((CliResult process, _) in both)
            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);

        Assert.Equal(["start", "start", "end", "end"], File.ReadAllLines(repo.MarkerLog));
    }

    [Fact]
    public async Task AGateThatOutlastsTheRunsTimeoutAtOneIsAFailedReceiptNotAnExceptionAsync()
    {
        repo.WriteCast(maxParallel: 1);

        await using (await RoleConcurrencyGate.AcquireAsync(repo.Repo, RoleConcurrencyGate.KeyFor("default", "builder"), 1, TimeSpan.FromSeconds(5), Ct))
        {
            (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--timeout", "1");

            Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
            Assert.Equal("failed", Str(result, "status"));
            Assert.Contains("all 1 'default__builder' slots", Str(result, "error"), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
            Assert.Equal("failed", Str(repo.ReadResult(Str(result, "job_id")), "status"));
        }

        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
        using FileStream slot = new(SlotFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(slot);
    }

    // review F1: CastBuilder stored the questionnaire's default answer `1` as null, so every cast made by
    // `cast create` / `cast new` / MCP cast_create had no cap and no gate at all.
    [Fact]
    public async Task ACastMadeByTheQuestionnaireWithTheAnswerOneIsGatedAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus","builder_max_parallel":"1"}""");
        CliResult created = await repo.RunAsync("cast", "create", "--answers", "answers.json");
        Assert.True(created.ExitCode == ExitCodes.Ok, created.Stderr);
        using (JsonDocument cast = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.Repo, ".claustrum", "casts", "default.json"))))
            Assert.Equal(1, cast.RootElement.GetProperty("roles").GetProperty("builder").GetProperty("max_parallel").GetInt32());

        await using (await RoleConcurrencyGate.AcquireAsync(repo.Repo, RoleConcurrencyGate.KeyFor("default", "builder"), 1, TimeSpan.FromSeconds(5), Ct))
        {
            (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--timeout", "1");

            Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
            Assert.Contains("all 1 'default__builder' slots", Str(result, "error"), StringComparison.Ordinal);
        }
    }

    // "Before the gate, so a typo is answered at once and not after a wait for a slot": every slot is held, and a
    // `--branch` that names no branch still comes back with that reason, not with the slot wait's.
    [Fact]
    public async Task ABranchThatDoesNotExistIsAnsweredBeforeTheWaitForASlotAsync()
    {
        repo.WriteCast(maxParallel: 1);

        await using (await RoleConcurrencyGate.AcquireAsync(repo.Repo, RoleConcurrencyGate.KeyFor("default", "builder"), 1, TimeSpan.FromSeconds(5), Ct))
        {
            (CliResult process, JsonElement result) = await repo.RunBuilderAsync("--branch", "nosuch", "--timeout", "1");

            Assert.Equal(ExitCodes.BackendFailure, process.ExitCode);
            Assert.Contains("--branch nosuch: no local branch of that name", Str(result, "error"), StringComparison.Ordinal);
            Assert.DoesNotContain("slots", Str(result, "error"), StringComparison.Ordinal);
        }
    }

    // F7: a blind-gate rejection mints nothing on any path — no job directory, no slot, no worktree.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ABlindGateRejectionUnderAMaxParallelCapLeavesNoJobDirectoryNoSlotAndNoWorktreeAsync(int cap)
    {
        repo.WriteCast(cap, role: "code-reviewer");

        CliResult rejected = await repo.RunAsync("run", "code-reviewer", "--brief", "## Task\nreview it\n\n## Context\nwhy we did it\n");

        Assert.Equal(ExitCodes.Usage, rejected.ExitCode);
        Assert.Contains("blind", rejected.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(repo.JobsRoot));
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "locks")));
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
        Assert.Single(TestGit.WorktreePaths(repo.Repo));
    }

    // The `--branch` path used to free the finished worktree and cut a new one before the blind gate ran.
    [Fact]
    public async Task ABlindGateRejectionWithABranchFreesNoWorktreeAndMintsNoJobAsync()
    {
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("first.txt", "first")] });
        (_, JsonElement first) = await repo.RunBuilderAsync();
        string firstId = Str(first, "job_id");
        string[] jobsBefore = [.. Directory.GetDirectories(repo.JobsRoot)];
        string[] locksBefore = [.. Directory.GetFiles(Path.Combine(repo.Repo, ".claustrum", "locks"))];
        repo.WriteCast(maxParallel: 2, role: "code-reviewer");

        CliResult rejected = await repo.RunAsync("run", "code-reviewer", "--branch", Str(first, "branch"), "--brief", "## Task\nreview it\n\n## Plan\nstep 1\n");

        Assert.Equal(ExitCodes.Usage, rejected.ExitCode);
        Assert.Equal(jobsBefore, Directory.GetDirectories(repo.JobsRoot));
        Assert.Equal(locksBefore, Directory.GetFiles(Path.Combine(repo.Repo, ".claustrum", "locks")));
        Assert.True(Directory.Exists(repo.WorktreePath(firstId)));
        Assert.Equal([firstId], Directory.GetDirectories(Path.Combine(repo.Repo, ".claustrum", "worktrees")).Select(Path.GetFileName));
    }
}
