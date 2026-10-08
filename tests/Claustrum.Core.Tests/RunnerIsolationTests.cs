using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// Runner on an isolated run (RunOptions.Worktree, set by DelegateEngine for max_parallel > 1 or --branch):
// #60 stamps worktree/branch into the result.json it writes, #61 commits the role's leftovers on the job's
// branch, #62 tells the role where it is, F6/R5/T3 keep it from committing or reporting anywhere but its own
// worktree. A ScriptedBackend runs a real shell command as the "role", in a real worktree of a real repo.
public sealed class RunnerIsolationTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-worktree-");
    private readonly string homeDir;
    private readonly string repo;

    public RunnerIsolationTests()
    {
        homeDir = scratch.CreateDirectory();
        repo = scratch.CreateSeededRepo();
    }

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParsedOutput WithReport(string reportJson) =>
        new($"done\n```claustrum-report\n{reportJson}\n```", SessionId: null, CostUsd: null, Usage: null, ReportedEdits: [], Raw: null, IsError: false);

    // `printf` in sh, `echo` in cmd: both leave one line in the file and nothing else.
    private static ScriptedBackend Writes(string fileName, string content, ParsedOutput? parsed = null) =>
        ScriptedBackend.Shell($"printf '{content}\\n' > {fileName}", $"echo {content}> {fileName}", parsed);

    private static ScriptedBackend Sleeps(string unixPrefix, string windowsPrefix) =>
        ScriptedBackend.Shell($"{unixPrefix} && sleep 20", $"{windowsPrefix} && ping -n 21 127.0.0.1 > nul");

    private static ResolvedRole MakeRole(bool blind = false, bool hasReport = false) =>
        new("builder", "system prompt", "scripted", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), blind, hasReport);

    private Runner NewRunner(IBackend backend)
    {
        HomeRedirectPlatform platform = new(homeDir);
        return new Runner(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
    }

    private static RunRequest MakeRequest(string cwd, string brief = "do it", TimeSpan? timeout = null) => new(
        Role: "builder", Brief: brief, BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
        Permission: null, BudgetUsd: null, Timeout: timeout, ResumeSession: null, AttachFiles: [], Env: [], Stream: false);

    private async Task<(RunResult Result, JobWorktreeInfo Worktree)> RunIsolatedAsync(
        IBackend backend, ResolvedRole? role = null, TimeSpan? timeout = null, string brief = "do it")
    {
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        RunResult result = await RunWithAsync(backend, worktree, role, timeout, brief);
        return (result, worktree);
    }

    private async Task<RunResult> RunWithAsync(
        IBackend backend, JobWorktreeInfo worktree, ResolvedRole? role = null, TimeSpan? timeout = null, string brief = "do it") =>
        await NewRunner(backend).RunAsync(
            MakeRequest(worktree.Path, brief, timeout), role ?? MakeRole(), new RunOptions(DiffByteCapBytes: 200_000, Worktree: worktree), Ct);

    private static JsonElement ReadResultJson(RunResult result)
    {
        string path = Path.Combine(Path.GetDirectoryName(result.LogPath) ?? "", "result.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    // #60: DelegateEngine used to add worktree/branch with a `with { … }` on the result Runner returned,
    // after FinishAsync had written result.json — so `jobs show` and the file named no branch.
    [Fact]
    public async Task ResultJsonOnDiskNamesTheWorktreeBranchAndCommitOfAnIsolatedRunAsync()
    {
        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(Writes("made.txt", "made by the role"));

        JsonElement onDisk = ReadResultJson(result);
        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.Equal(worktree.Path, onDisk.GetProperty("worktree").GetString());
        Assert.Equal("claustrum/job-1", onDisk.GetProperty("branch").GetString());
        Assert.Equal(tip, onDisk.GetProperty("commit").GetString());
        Assert.Equal(tip, result.Commit);
        Assert.Equal(worktree.Path, result.Worktree);
        Assert.Equal("claustrum/job-1", result.Branch);
    }

    // The funnel stamps both on every result of an isolated run, a backend_missing after the worktree was cut included.
    [Fact]
    public async Task ABackendMissingAfterTheWorktreeWasCutStillCarriesWorktreeAndBranchOnDiskAsync()
    {
        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(ScriptedBackend.NotOnPath());

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        JsonElement onDisk = ReadResultJson(result);
        Assert.Equal(worktree.Path, onDisk.GetProperty("worktree").GetString());
        Assert.Equal("claustrum/job-1", onDisk.GetProperty("branch").GetString());
        Assert.Equal(JsonValueKind.Null, onDisk.GetProperty("commit").ValueKind);
    }

    [Fact]
    public async Task ARefusalNeverHasAWorktreeBranchOrCommitOnDiskAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        JobPaths job = JobDirectory.Create(platform);

        RunResult result = await Runner.RefuseAsync(job, MakeRole(), RunStatus.Failed, "--branch nosuch: no local branch of that name");

        JsonElement onDisk = ReadResultJson(result);
        Assert.Equal("failed", onDisk.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, onDisk.GetProperty("worktree").ValueKind);
        Assert.Equal(JsonValueKind.Null, onDisk.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, onDisk.GetProperty("commit").ValueKind);
        Assert.Equal("--branch nosuch: no local branch of that name", onDisk.GetProperty("error").GetString());
    }

    // The report's summary is the line an architect reads in `git log` before rebasing the branch.
    [Fact]
    public async Task LeftoversAreCommittedOnceWithTheRoleAndJobIdAsSubjectAndTheReportSummaryAsBodyAsync()
    {
        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(Writes("made.txt", "made by the role", WithReport(/*lang=json,strict*/ """{"summary":"added made.txt"}""")));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(1, GitRepo.CommitCount(repo, $"{worktree.BaseCommit}..refs/heads/claustrum/job-1"));
        Assert.Equal($"claustrum builder {result.JobId}\n\nadded made.txt", GitRepo.Run(repo, "log", "-1", "--format=%B", "refs/heads/claustrum/job-1"));
        Assert.Equal("", GitRepo.Status(worktree.Path));
        ChangedFile file = Assert.Single(result.ChangedFiles);
        Assert.Equal("made.txt", file.Path);
        Assert.Contains("+made by the role", result.Diff, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(/*lang=json,strict*/ """{"summary":5}""")]
    [InlineData(/*lang=json,strict*/ """{"summary":"   "}""")]
    [InlineData(/*lang=json,strict*/ """{"other":"x"}""")]
    [InlineData(/*lang=json,strict*/ """[1,2]""")]
    public async Task ACommitWhoseReportHasNoUsableSummaryIsJustTheSubjectAsync(string reportJson)
    {
        (RunResult result, _) = await RunIsolatedAsync(Writes("made.txt", "made by the role", WithReport(reportJson)));

        Assert.Equal($"claustrum builder {result.JobId}", GitRepo.Run(repo, "log", "-1", "--format=%B", "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task ARunThatChangedNothingMakesNoCommitAndReportsANullCommitAsync()
    {
        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(ScriptedBackend.Success());

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Empty(result.ChangedFiles);
        Assert.Null(result.Diff);
        Assert.Empty(result.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task ARoleThatCommittedItselfHasItsOwnCommitAndItsChangesOnTheReceiptAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Shell(
            "printf 'x\\n' > own.txt && git add -A && git commit -q -m role-commit",
            "echo x> own.txt && git add -A && git commit -q -m role-commit");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend);

        string tip = GitRepo.RevParse(repo, "refs/heads/claustrum/job-1");
        Assert.Equal(tip, result.Commit);
        Assert.Equal("role-commit", GitRepo.Run(repo, "log", "-1", "--format=%s", tip));
        Assert.Equal(1, GitRepo.CommitCount(repo, $"{worktree.BaseCommit}..{tip}"));
        ChangedFile file = Assert.Single(result.ChangedFiles);
        Assert.Equal("own.txt", file.Path);
        Assert.False(string.IsNullOrEmpty(result.Diff));
    }

    // Partial work on the job's own branch beats partial work in a worktree directory, and `status` still
    // says what happened.
    [Fact]
    public async Task AFailedRunStillCommitsWhatItLeftAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Shell("printf 'x\\n' > partial.txt; exit 1", "echo x> partial.txt & exit /b 1");

        (RunResult result, _) = await RunIsolatedAsync(backend);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), result.Commit);
        Assert.Contains("partial.txt", GitRepo.Run(repo, "show", "--name-only", "--format=", "refs/heads/claustrum/job-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimedOutRunStillCommitsWhatItLeftAsync()
    {
        ScriptedBackend backend = Sleeps("printf 'x\\n' > partial.txt", "echo x> partial.txt");

        (RunResult result, _) = await RunIsolatedAsync(backend, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(RunStatus.Timeout, result.Status);
        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), result.Commit);
        Assert.Contains("partial.txt", GitRepo.Run(repo, "show", "--name-only", "--format=", "refs/heads/claustrum/job-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledRunStillCommitsWhatItLeftAsync()
    {
        ScriptedBackend backend = Sleeps("printf 'x\\n' > partial.txt", "echo x> partial.txt");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(2));

        RunResult result = await NewRunner(backend).RunAsync(
            MakeRequest(worktree.Path), MakeRole(), new RunOptions(DiffByteCapBytes: 200_000, Worktree: worktree), cancel.Token);

        Assert.Equal(RunStatus.Cancelled, result.Status);
        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), result.Commit);
    }

    // The runner commits for an isolated run only. An in-place run edits the operator's own tree, and what
    // goes into the operator's branch is the operator's call.
    [Fact]
    public async Task AnInPlaceRunNeverCommitsAndItsBriefCarriesNoIsolationTrailerAsync()
    {
        ScriptedBackend backend = Writes("made.txt", "made by the role");
        string before = GitRepo.Head(repo);

        RunResult result = await NewRunner(backend).RunAsync(
            MakeRequest(repo), MakeRole(), new RunOptions(DiffByteCapBytes: 200_000), CancellationToken.None);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Null(result.Worktree);
        Assert.Null(result.Branch);
        Assert.Equal(before, GitRepo.Head(repo));
        Assert.Equal("?? made.txt", GitRepo.Status(repo));
        Assert.Equal("do it", backend.LastRun?.Brief);
        Assert.Contains(result.ChangedFiles, f => f.Path == "made.txt");
    }

    [Fact]
    public async Task ACommitAHookRefusesCostsAWarningAndNeverTheRunsStatusAsync()
    {
        GitRepo.WriteHook(repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(Writes("made.txt", "made by the role"));

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Contains("work left uncommitted on claustrum/job-1: hook says no", result.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Contains(result.ChangedFiles, f => f.Path == "made.txt");
        Assert.True(File.Exists(Path.Combine(worktree.Path, "made.txt")));
    }

    [Fact]
    public async Task ADeltaThatFailsAfterTheCommitKeepsTheCommitAndWarnsAsync()
    {
        JobWorktreeInfo real = await JobWorktree.AddAsync(repo, "job-1", Ct);
        JobWorktreeInfo bogusBase = real with { BaseCommit = new string('0', 40) };

        RunResult result = await RunWithAsync(Writes("made.txt", "made by the role"), bogusBase);

        Assert.Equal(GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"), result.Commit);
        Assert.Contains(result.Warnings, w => w.StartsWith("receipt delta unavailable, snapshot kept: ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("work left uncommitted", StringComparison.Ordinal));
        Assert.Contains(result.ChangedFiles, f => f.Path == "made.txt");
    }

    // R5 + T3: the job's own worktree, only off its branch, keeps the snapshot read inside it and gets
    // no commit.
    [Fact]
    public async Task ARunThatSwitchedItsOwnWorktreeToAnotherBranchKeepsItsChangesAndGetsNoCommitAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Shell(
            "git switch -q -c other && printf 'x\\n' > made.txt",
            "git switch -q -c other && echo x> made.txt");

        (RunResult result, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Null(result.Commit);
        Assert.Contains(result.ChangedFiles, f => f.Path == "made.txt");
        Assert.Contains($"work left uncommitted: {worktree.Path} is on refs/heads/other, not claustrum/job-1", result.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/other"));
    }

    // R5: the worktree was removed and the role's next write recreated its path as a plain directory inside
    // the main checkout. git resolves every command there to the operator's checkout, so the receipt used to
    // carry the operator's uncommitted edits as "changed files", and the runner committed them (F6).
    [Fact]
    public async Task ARecreatedPlainDirectoryReportsNothingAndNeverCommitsTheOperatorsFilesAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the role deletes the directory it runs in; Windows refuses that for a running process.");
            return;
        }

        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        File.WriteAllText(Path.Combine(repo, "seed.txt"), "the operator's edit\n");
        File.WriteAllText(Path.Combine(repo, "mine.txt"), "the operator's new file\n");
        string mainHead = GitRepo.Head(repo);
        string mainStatus = GitRepo.Status(repo);
        ScriptedBackend backend = new("sh", ["-c", $"rm -rf '{worktree.Path}' && mkdir -p '{worktree.Path}' && printf 'x\\n' > '{worktree.Path}/lost.txt'"], WithReport(/*lang=json,strict*/ """{"summary":"kept"}"""));

        RunResult result = await RunWithAsync(backend, worktree);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Empty(result.ChangedFiles);
        Assert.Null(result.Diff);
        Assert.Null(result.Commit);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        string warning = Assert.Single(result.Warnings);
        Assert.StartsWith($"work left uncommitted: {worktree.Path} is not the job worktree on claustrum/job-1 (", warning, StringComparison.Ordinal);
        Assert.Equal(mainHead, GitRepo.Head(repo));
        Assert.Equal(mainStatus, GitRepo.Status(repo));
        Assert.Equal("the operator's edit\n", File.ReadAllText(Path.Combine(repo, "seed.txt")));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // R5: a worktree removed and not recreated used to throw out of the after-snapshot and end as
    // `status: failed` with the report lost. The run's own status and report are kept.
    [Fact]
    public async Task AWorktreeThatIsGoneKeepsTheRunsStatusAndReportAndSaysSoAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the role deletes the directory it runs in; Windows refuses that for a running process.");
            return;
        }

        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        ScriptedBackend backend = new("sh", ["-c", $"rm -rf '{worktree.Path}'"], WithReport(/*lang=json,strict*/ """{"summary":"kept"}"""));

        RunResult result = await RunWithAsync(backend, worktree);

        Assert.Equal(RunStatus.Success, result.Status);
        Assert.Equal(ReportStatus.Ok, result.ReportStatus);
        Assert.Equal("kept", result.Report?.Data?.GetProperty("summary").GetString());
        Assert.Empty(result.ChangedFiles);
        Assert.Null(result.Commit);
        Assert.Equal([$"work left uncommitted: {worktree.Path} is not the job worktree on claustrum/job-1 (the directory is gone)"], result.Warnings);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.LogPath) ?? "", "result.json")));
    }

    // #62: the trailer names the worktree, its branch and the checkout a delegate must never touch, ahead of
    // the report trailer, which keeps the last position.
    [Fact]
    public async Task AnIsolatedBriefCarriesTheIsolationTrailerAheadOfTheReportTrailerAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();

        (_, JobWorktreeInfo worktree) = await RunIsolatedAsync(backend, MakeRole(hasReport: true));

        string brief = backend.LastRun?.Brief ?? "";
        Assert.StartsWith("do it\n\n---\nYou are working in an isolated git worktree", brief, StringComparison.Ordinal);
        Assert.Contains($"`{worktree.Path}`", brief, StringComparison.Ordinal);
        Assert.Contains("on branch `claustrum/job-1`", brief, StringComparison.Ordinal);
        Assert.Contains($"The repository's main checkout is `{repo}`: never cd into it", brief, StringComparison.Ordinal);
        Assert.Contains("`git checkout` and `git switch` are denied for this whole run", brief, StringComparison.Ordinal);
        Assert.Contains("you may commit yourself; never push", brief, StringComparison.Ordinal);
        Assert.EndsWith("a reply without it is rejected.", brief, StringComparison.Ordinal);
        Assert.True(
            brief.IndexOf("isolated git worktree", StringComparison.Ordinal) < brief.IndexOf("claustrum-report", StringComparison.Ordinal),
            "the report trailer must stay last");
    }

    [Fact]
    public async Task AnIsolatedBriefWithoutAReportRoleEndsWithTheIsolationTrailerAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();

        await RunIsolatedAsync(backend, MakeRole(hasReport: false));

        Assert.EndsWith("(you may commit yourself; never push).", backend.LastRun?.Brief, StringComparison.Ordinal);
    }

    // The trailer is runner text appended after the gate, and has no `## ` heading of its own: a blind
    // role's isolated run is not rejected for it, and a brief that really carries rationale still is.
    [Fact]
    public async Task TheIsolationTrailerDoesNotTripTheBlindGateAsync()
    {
        ScriptedBackend backend = ScriptedBackend.Success();

        (RunResult result, _) = await RunIsolatedAsync(backend, MakeRole(blind: true, hasReport: true), brief: "## Task\nreview the diff\n\n## Scope\nsrc/\n");

        Assert.Equal(RunStatus.Success, result.Status);
        string brief = backend.LastRun?.Brief ?? "";
        string trailer = brief["## Task\nreview the diff\n\n## Scope\nsrc/\n".Length..];
        Assert.DoesNotContain("\n## ", trailer, StringComparison.Ordinal);
        Assert.DoesNotContain("## Context", trailer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlindBriefWithRationaleIsStillRejectedWhenTheRunIsIsolatedAsync()
    {
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);

        await Assert.ThrowsAsync<BlindGateException>(
            () => RunWithAsync(ScriptedBackend.Success(), worktree, MakeRole(blind: true), brief: "## Task\nreview\n\n## Context\nwhy we did it\n"));

        Assert.False(Directory.Exists(Path.Combine(homeDir, ".claustrum", "jobs")));
    }

    // F7: Validate is the Runner-owned half of "a rejected brief mints nothing", for a caller that would
    // otherwise cut a worktree or wait for a slot first.
    [Fact]
    public void ValidateRejectsAZeroTimeoutAndABlindBriefWithRationaleAndMintsNothing()
    {
        HomeRedirectPlatform platform = new(homeDir);

        Assert.Throws<RunRequestException>(() => Runner.Validate(MakeRequest(repo, timeout: TimeSpan.Zero), MakeRole(), platform));
        Assert.Throws<BlindGateException>(() => Runner.Validate(MakeRequest(repo, brief: "## Task\nx\n## Plan\ny\n"), MakeRole(blind: true), platform));
        Runner.Validate(MakeRequest(repo, brief: "## Task\nx\n## Scope\ny\n"), MakeRole(blind: true), platform);
        Assert.False(Directory.Exists(Path.Combine(homeDir, ".claustrum")));
    }

    // F10: RefuseAsync owns the reservation it is handed. Inside a tree a refusal after admission closes
    // the ledger entry as done at $0 (the next admission must not read it as abandoned).
    [Fact]
    public async Task ARefusalAfterAdmissionClosesTheLedgerEntryAsDoneAtZeroAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        JobPaths job = JobDirectory.Create(platform);
        JobTreeBudget tree = new("tree-refusal", 5.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, job.Id, "builder", requestedCap: null);
        Assert.True(admission.Admitted);

        RunResult result = await Runner.RefuseAsync(job, MakeRole(), RunStatus.Failed, "--branch x: refused", admission.Reservation);

        Assert.Equal(RunStatus.Failed, result.Status);
        BudgetLedgerState state = await BudgetLedger.ReadAsync(platform, tree.TreeId);
        BudgetLedgerRow row = Assert.Single(state.Rows);
        Assert.Equal(BudgetEntryState.Done, row.State);
        Assert.Equal(0m, row.Entry.Cost);
        Assert.Equal(0m, state.Reserved);
        Assert.Equal(5.00m, await BudgetLedger.PeekRemainingAsync(platform, tree));
    }

    // F10: CompleteAsync releases the `.live` handle only after a ledger write that succeeded, and the
    // charge swallows a failed one — in the long-lived MCP server nothing else disposed the reservation.
    [Fact]
    public async Task ARefusalWhoseLedgerWriteFailsStillReleasesTheLiveHandleAndWritesTheResultAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        JobPaths job = JobDirectory.Create(platform);
        JobTreeBudget tree = new("tree-broken", 5.00m);
        BudgetAdmission admission = await BudgetLedger.AdmitAsync(platform, tree, job.Id, "builder", requestedCap: null);
        string ledger = BudgetLedger.DirectoryFor(platform, tree.TreeId);
        string entry = Path.Combine(ledger, $"{job.Id}.json");
        string live = Path.Combine(ledger, $"{job.Id}.live");
        File.Delete(entry);
        Directory.CreateDirectory(entry);
        Assert.Throws<IOException>(() => new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None));

        RunResult result = await Runner.RefuseAsync(job, MakeRole(), RunStatus.Failed, "--branch x: refused", admission.Reservation);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains(result.Warnings, w => w.StartsWith("budget ledger for tree 'tree-broken' not updated with this job's cost:", StringComparison.Ordinal));
        Assert.True(File.Exists(job.ResultJson));
        using FileStream released = new(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(released);
    }

    [Fact]
    public async Task ARefusalWithNoReservationTouchesNoLedgerAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        JobPaths job = JobDirectory.Create(platform);

        await Runner.RefuseAsync(job, MakeRole(), RunStatus.Failed, "refused");

        Assert.False(Directory.Exists(Path.Combine(homeDir, ".claustrum", "budget")));
    }
}
