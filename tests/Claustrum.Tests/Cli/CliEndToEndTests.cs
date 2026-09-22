using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The exit-code half of docs/PLAN.md §A5 and §B4, driven through the real built binary: several of
// the 2026-09-14 review findings (a JSONC .vscode/mcp.json, `cast create --answers`, a cast file
// with no `roles`) only show as the wrong exit code or a raw stack trace at the process door, which
// no in-process test observes. `claustrum` is on the test output path via the project reference, so
// this needs no publish step. CLAUSTRUM_HOME keeps every job out of the real ~/.claustrum.
public sealed partial class CliEndToEndTests : IDisposable
{
    private const int Ok = 0;
    private const int Usage = 2;

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-e2e-").FullName;
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-e2e-home-").FullName;

    public void Dispose()
    {
        // cwd holds a real git repo in several of these: git's read-only loose objects defeat a
        // plain recursive delete on Windows.
        TempTree.Delete(cwd);
        TempTree.Delete(home);
    }

    [Fact]
    public async Task SyncCheckOnAFreshRepoExitsTwoAndNamesWhatIsMissingAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("missing:", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncThenSyncCheckExitsZeroAsync()
    {
        Assert.Equal(Ok, (await RunAsync("sync")).ExitCode);

        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("stale:", stdout, StringComparison.Ordinal);
    }

    // The third branch of --check, between "missing" and clean: a generated file that still carries
    // the marker but no longer matches what the library renders.
    [Fact]
    public async Task SyncCheckReportsAStaleGeneratedFileAsync()
    {
        Assert.Equal(Ok, (await RunAsync("sync")).ExitCode);
        string agent = Path.Combine(cwd, ".claude", "agents", "builder.md");
        File.WriteAllText(agent, File.ReadAllText(agent) + "\nhand-edited tail\n");

        (int exitCode, string stdout, _) = await RunAsync("sync", "--check");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("stale:", stdout, StringComparison.Ordinal);
        Assert.Contains("hand-edited tail", File.ReadAllText(agent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDryRunPrintsAUnifiedDiffAndWritesNothingAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--dry-run");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("--- /dev/null", stdout, StringComparison.Ordinal);
        Assert.Contains("+++ b/", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyOpencodeWritesOpencodeFilesNotClaudeAsync()
    {
        (int exitCode, _, _) = await RunAsync("sync", "--only", "opencode", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "command", "claustrum.md")));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyCopilotWritesGithubFilesAsync()
    {
        (int exitCode, _, _) = await RunAsync("sync", "--only", "copilot", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "agents", "builder.agent.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncOnlyClaudeAndOpencodeWritesBothAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--only", "claude,opencode", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".claude", "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
        Assert.Contains("written:", stdout, StringComparison.Ordinal);
    }

    // issue #25: CursorSync wired through the same --only door as every other harness.
    [Fact]
    public async Task SyncOnlyCursorWritesAgentsSkillAndMcpJsonAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("sync", "--only", "cursor", "--roles", "builder");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "skills", "claustrum", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "mcp.json")));
        Assert.Contains("written:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncOnlyCursorThenCheckRoundTripsAsync()
    {
        Assert.Equal(Ok, (await RunAsync("sync", "--only", "cursor", "--roles", "builder")).ExitCode);

        (int exitCode, string stdout, _) = await RunAsync("sync", "--only", "cursor", "--roles", "builder", "--check");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("stale:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("missing:", stdout, StringComparison.Ordinal);
    }

    // "cursor" used to be the unsupported example here; it is a real target since issue #25, so a
    // genuinely unknown name proves the same "unknown harness -> exit 2, names it, writes nothing"
    // coverage without going stale the moment a fifth harness is added.
    [Fact]
    public async Task SyncOnlyUnsupportedHarnessExitsTwoNamingItAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("sync", "--only", "windsurf");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("windsurf", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public async Task SyncRefusesBothCheckAndDryRunAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("sync", "--check", "--dry-run");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("not both", stderr, StringComparison.Ordinal);
    }

    // Finding #1 at the process door: a JSONC `.vscode/mcp.json` used to exit 1; genuinely malformed
    // JSON has to exit 2 with the file named, not a raw JsonException.
    [Fact]
    public async Task SyncToleratesAJsoncVsCodeMcpFileAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".vscode"));
        File.WriteAllText(Path.Combine(cwd, ".vscode", "mcp.json"), /*lang=json*/ """
            {
              // Managed by hand: see the team wiki
              "servers": { "fetch": { "type": "stdio", "command": "uvx", "args": ["mcp-server-fetch"] }, },
            }
            """);

        (int exitCode, _, string stderr) = await RunAsync("sync");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("fetch", File.ReadAllText(Path.Combine(cwd, ".vscode", "mcp.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".claustrum", "sync-manifest.json")), $"sync did not run to completion: {stderr}");
    }

    [Fact]
    public async Task SyncOnMalformedJsonExitsTwoNamingTheFileAsync()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), "{ this is not json");

        (int exitCode, _, string stderr) = await RunAsync("sync");

        Assert.Equal(Usage, exitCode);
        Assert.Contains(".mcp.json", stderr, StringComparison.Ordinal);
    }

    // Finding #2: the generated skill tells agents to run exactly this, and the CLI used to reject
    // it ("Unrecognized command or argument '--answers'").
    [Fact]
    public async Task CastCreateAcceptsAnswersAsAnOptionAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:opus","budget":"no cap"}""");

        (int exitCode, _, string stderr) = await RunAsync("cast", "create", "--answers", "answers.json");

        Assert.Equal(Ok, exitCode);
        Assert.True(File.Exists(Path.Combine(cwd, ".claustrum", "casts", "default.json")), stderr);
    }

    // Finding #8. The guard against finding #2 masking this one: an argument-parse failure also
    // exits 2 and echoes the file name, so assert the message is the JSON error, not "Unrecognized".
    [Fact]
    public async Task CastCreateOnAMalformedAnswersFileExitsTwoNamingTheFileAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":1}""");

        (int exitCode, _, string stderr) = await RunAsync("cast", "create", "--answers", "answers.json");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("answers.json", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrecognized", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastCreateShowUseAndListRoundTripAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "answers.json"), /*lang=json,strict*/ """{"builder":"claude:haiku","budget":"3"}""");
        Assert.Equal(Ok, (await RunAsync("cast", "create", "--answers", "answers.json", "--name", "staging")).ExitCode);

        (int showExit, string showOut, _) = await RunAsync("cast", "show", "staging");
        Assert.Equal(Ok, showExit);
        Assert.Contains("claude:haiku", showOut, StringComparison.Ordinal);

        Assert.Equal(Ok, (await RunAsync("cast", "use", "staging")).ExitCode);

        (int listExit, string listOut, _) = await RunAsync("cast", "list");
        Assert.Equal(Ok, listExit);
        Assert.Contains("default", listOut, StringComparison.Ordinal);
        Assert.Contains("staging", listOut, StringComparison.Ordinal);

        // `use` copies the cast over default.json — the copy must keep the source's role entries.
        Assert.Contains("claude:haiku", (await RunAsync("cast", "show", "default")).Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CastShowForAnUnknownCastExitsTwoAsync()
    {
        (int exitCode, _, string stderr) = await RunAsync("cast", "show", "nope");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("nope", stderr, StringComparison.Ordinal);
    }

    // Finding #3: default.json applies itself to every run, so a cast file with no "roles" key used
    // to NRE ("Value cannot be null") on every run in the repo instead of naming the broken file.
    [Fact]
    public async Task RunWithACastFileMissingRolesExitsTwoNamingTheFileAsync()
    {
        const string noRoles = /*lang=json,strict*/ """{"name":"default","library":"1.0.0","architect":{"mode":"host"},"budget_usd":null}""";
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), noRoles);

        (int exitCode, _, string stderr) = await RunAsync("run", "builder", "--brief", "hello");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("roles", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Value cannot be null", stderr, StringComparison.Ordinal);
    }

    // Finding #7 at the process door: the gate fires before anything spawns, and must leave no job
    // directory behind (JobDirectory.Create used to run as an argument, ahead of the gate).
    [Fact]
    public async Task BlindGateRejectionExitsTwoAndLeavesNoJobDirectoryAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "brief.md"), "## Task\nreview it\n\n## Context\nwhy we did it\n");

        (int exitCode, _, string stderr) = await RunAsync("run", "code-reviewer", "--brief-file", "brief.md");

        Assert.Equal(Usage, exitCode);
        Assert.Contains("blind", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));
    }

    // docs/PLAN.md §D4 "claustrum jobs clean removes worktrees of finished jobs". backend
    // "nonexistent" reaches Runner's registry check *after* DelegateEngine has already created the
    // worktree (max_parallel > 1), so the job still finishes (status backend_missing, result.json
    // written) with a real worktree on disk to clean up — no real backend install needed, same trick
    // DelegateEngineTests uses in-process.
    [Fact]
    public async Task JobsCleanRemovesAFinishedWorktreeAndKeepsItsBranchAsync()
    {
        RunGit(cwd, "init", "-q");
        RunGit(cwd, "config", "user.email", "test@example.com");
        RunGit(cwd, "config", "user.name", "claustrum-tests");
        File.WriteAllText(Path.Combine(cwd, "seed.txt"), "seed\n");
        RunGit(cwd, "add", "-A");
        RunGit(cwd, "commit", "-q", "-m", "seed");
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "casts"));
        File.WriteAllText(Path.Combine(cwd, ".claustrum", "casts", "default.json"), /*lang=json,strict*/
            """{"name":"default","library":"1.0.0","architect":{"mode":"host"},"roles":{"builder":{"backend":"nonexistent","max_parallel":2}},"budget_usd":null}""");

        (int runExit, string runOut, string runErr) = await RunAsync("run", "builder", "--brief", "hi", "--json");
        Assert.Equal(ExitCodes.BackendMissing, runExit);
        string jobId = JsonDocument.Parse(runOut).RootElement.GetProperty("job_id").GetString()!;
        string worktreePath = Path.Combine(cwd, ".claustrum", "worktrees", jobId);
        Assert.True(Directory.Exists(worktreePath), runErr);

        (int cleanExit, string cleanOut, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, cleanExit);
        Assert.Contains(jobId, cleanOut, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktreePath));
        Assert.Contains($"claustrum/{jobId}", ListBranches(cwd));
    }

    // Review finding: `jobs clean` only ever removed worktrees whose job wrote a result.json, so a
    // worktree left by a hard kill (or one whose job directory jobs.keep_last later pruned) waited
    // forever for a signal that could never arrive.
    [Fact]
    public async Task JobsCleanRemovesAWorktreeWhoseJobDirectoryIsGoneAsync()
    {
        SeedRepo();
        RunGit(cwd, "worktree", "add", Path.Combine(".claustrum", "worktrees", "20260101-000000-deadbeef"), "-b", "claustrum/20260101-000000-deadbeef");

        // The job store exists but this job's directory does not — hard-killed before writing a
        // result, or pruned by jobs.keep_last. (An absent store means "wrong CLAUSTRUM_HOME" and is
        // deliberately not cleanable; JobsCleanLeavesWorktreesAloneWhenTheJobRootItselfIsMissing.)
        Directory.CreateDirectory(Path.Combine(home, "jobs"));

        (int exitCode, string stdout, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("20260101-000000-deadbeef", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees", "20260101-000000-deadbeef")));
    }

    // The missing-job-directory rule must not fire when the job root itself is absent: a `jobs clean`
    // pointed at a different CLAUSTRUM_HOME than the run used would otherwise find every job
    // "missing" and force-remove a live worktree along with its uncommitted work.
    [Fact]
    public async Task JobsCleanLeavesWorktreesAloneWhenTheJobRootItselfIsMissingAsync()
    {
        SeedRepo();
        RunGit(cwd, "worktree", "add", Path.Combine(".claustrum", "worktrees", "20260101-000000-cafecafe"), "-b", "claustrum/20260101-000000-cafecafe");

        // No run has happened under this CLAUSTRUM_HOME, so the job root does not exist at all —
        // exactly what a `clean` pointed at the wrong home looks like.
        Assert.False(Directory.Exists(Path.Combine(home, "jobs")));

        (int exitCode, string stdout, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("nothing to clean", stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees", "20260101-000000-cafecafe")));
    }

    // Review finding: one directory git no longer recognises used to abort the whole sweep, so every
    // stale worktree after it survived — and every rerun stopped at the same one.
    [Fact]
    public async Task JobsCleanReportsAnUnremovableWorktreeAndStillClearsTheRestAsync()
    {
        SeedRepo();
        RunGit(cwd, "worktree", "add", Path.Combine(".claustrum", "worktrees", "20260101-000000-99999999"), "-b", "claustrum/20260101-000000-99999999");

        // The job store exists but this job's directory does not — hard-killed before writing a
        // result, or pruned by jobs.keep_last. (An absent store means "wrong CLAUSTRUM_HOME" and is
        // deliberately not cleanable; JobsCleanLeavesWorktreesAloneWhenTheJobRootItselfIsMissing.)
        Directory.CreateDirectory(Path.Combine(home, "jobs"));

        // Sorts before the real one, so an abort-on-first-failure sweep would never reach it.
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "worktrees", "20250101-000000-00000000"));

        (int exitCode, string stdout, string stderr) = await RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.BackendFailure, exitCode);
        Assert.Contains("20250101-000000-00000000", stderr, StringComparison.Ordinal);
        Assert.Contains("20260101-000000-99999999", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees", "20260101-000000-99999999")));
    }

    // Review finding: RoleConcurrencyGate's slot files are untracked and WorktreeSnapshot runs
    // `git status --untracked-files=all`, so an un-ignored .claustrum/locks/ put phantom lock files
    // in every later job's changed_files/diff.
    [Fact]
    public async Task InitIgnoresLocksAsWellAsWorktreesAsync()
    {
        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        string gitignore = await File.ReadAllTextAsync(Path.Combine(cwd, ".gitignore"), TestContext.Current.CancellationToken);
        Assert.Contains(".claustrum/worktrees/", gitignore, StringComparison.Ordinal);
        Assert.Contains(".claustrum/locks/", gitignore, StringComparison.Ordinal);
    }

    // A repo initialised before a rule existed has to gain that one line, not a second copy of the
    // whole block: the idempotency check used to key on `.claustrum/worktrees/` alone.
    [Fact]
    public async Task InitAddsOnlyTheMissingIgnoreLineToAnOlderGitignoreAsync()
    {
        await File.WriteAllTextAsync(
            Path.Combine(cwd, ".gitignore"),
            "bin/\n.claustrum/worktrees/\n.claustrum/briefs/\n",
            TestContext.Current.CancellationToken);

        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        string gitignore = await File.ReadAllTextAsync(Path.Combine(cwd, ".gitignore"), TestContext.Current.CancellationToken);
        Assert.Contains(".claustrum/locks/", gitignore, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(gitignore, ".claustrum/worktrees/"));
        Assert.Equal(1, CountOccurrences(gitignore, ".claustrum/briefs/"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private void SeedRepo()
    {
        RunGit(cwd, "init", "-q");
        RunGit(cwd, "config", "user.email", "test@example.com");
        RunGit(cwd, "config", "user.name", "claustrum-tests");
        File.WriteAllText(Path.Combine(cwd, "seed.txt"), "seed\n");
        RunGit(cwd, "add", "-A");
        RunGit(cwd, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task JobsCleanOnARepoWithNoWorktreesIsANoOpAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("jobs", "clean");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("no worktrees", stdout, StringComparison.Ordinal);
    }

    // §D4's tree budget through the real front door: CLAUSTRUM_PARENT_JOB names the tree, no cast is
    // needed (the built-in defaults.budget_usd, 5, is the tree's budget), and the seed entry below is
    // the on-disk ledger shape NOTES.md "Tree budget accounting is a file ledger" documents, written
    // by hand rather than through the production type so the test pins the actual file contract.
    [Fact]
    public async Task RunOverTheTreeRemainderExitsFiveNamingTheExceededBudgetAsync()
    {
        SeedBudgetLedgerEntry(home, "cli-tree-over", jobId: "seed", cap: 4.90m, cost: 4.90m);

        (int exitCode, string stdout, _) = await RunAsync(
            new Dictionary<string, string> { ["CLAUSTRUM_PARENT_JOB"] = "cli-tree-over" },
            "run", "builder", "--brief", "hi", "--backend", "nonexistent", "--budget", "0.5", "--json");

        Assert.Equal(ExitCodes.Budget, exitCode);
        JsonElement root = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("budget_exceeded", root.GetProperty("status").GetString());
        Assert.Contains("--budget 0.50 exceeds it", root.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWithinTheTreeRemainderIsClampedToItWithNoExplicitBudgetAsync()
    {
        SeedBudgetLedgerEntry(home, "cli-tree-clamp", jobId: "seed", cap: 4.90m, cost: 4.90m);

        (int exitCode, string stdout, _) = await RunAsync(
            new Dictionary<string, string> { ["CLAUSTRUM_PARENT_JOB"] = "cli-tree-clamp" },
            "run", "builder", "--brief", "hi", "--backend", "nonexistent", "--json");

        Assert.Equal(ExitCodes.BackendMissing, exitCode);
        string jobId = JsonDocument.Parse(stdout).RootElement.GetProperty("job_id").GetString()!;
        string requestJson = File.ReadAllText(Path.Combine(home, "jobs", jobId, "request.json"));
        // Compared numerically, not as a literal "0.10" substring: BudgetLedger's floor-to-cents
        // (Math.Floor(x * 100m) / 100m) normalizes decimal's own scale, and a quotient with no
        // significant second decimal digit — exactly this $0.10 case — serializes as "0.1", the same
        // way NOTES.md's own $0.12/$0.66 examples show only as many digits as the value needs.
        Assert.Equal(0.10m, JsonDocument.Parse(requestJson).RootElement.GetProperty("budget_usd").GetDecimal());
    }

    [Fact]
    public async Task JobsBudgetListsRowsWithCapCostAndSpentReservedLinesAsync()
    {
        SeedBudgetLedgerEntry(home, "cli-tree-list", jobId: "seed", cap: 1.25m, cost: 1.00m);

        (int exitCode, string stdout, _) = await RunAsync("jobs", "budget", "cli-tree-list");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("seed", stdout, StringComparison.Ordinal);
        Assert.Contains("cap", stdout, StringComparison.Ordinal);
        Assert.Contains("cost", stdout, StringComparison.Ordinal);
        Assert.Contains("spent     $1.00", stdout, StringComparison.Ordinal);
        Assert.Contains("reserved  $0.00", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobsBudgetResetDeletesTheLedgerButRefusesWhileAJobIsLiveAsync()
    {
        string directory = Path.Combine(home, "budget", "cli-tree-reset");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "running.json"),
            /*lang=json,strict*/ """{"job_id":"running","role":"builder","cap":1.00,"cost":null,"started_at":"2026-01-01T00:00:00Z","finished_at":null,"abandoned":false}""");

        using (new FileStream(Path.Combine(directory, "running.live"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            (int refusedExit, _, string refusedErr) = await RunAsync("jobs", "budget", "cli-tree-reset", "--reset");

            Assert.Equal(Usage, refusedExit);
            Assert.Contains("running", refusedErr, StringComparison.Ordinal);
            Assert.True(Directory.Exists(directory));
        }

        (int exitCode, string stdout, _) = await RunAsync("jobs", "budget", "cli-tree-reset", "--reset");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("reset", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task JobsBudgetOnATreeThatNeverExistedPrintsNoEntriesAndCreatesNothingAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("jobs", "budget", "cli-tree-never-existed");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("(no entries)", stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(home, "budget", "cli-tree-never-existed")));
    }

    private static void SeedBudgetLedgerEntry(string home, string tree, string jobId, decimal cap, decimal cost)
    {
        string directory = Path.Combine(home, "budget", tree);
        Directory.CreateDirectory(directory);
        string json = /*lang=json,strict*/
            $$"""{"job_id":"{{jobId}}","role":"builder","cap":{{cap}},"cost":{{cost}},"started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:00:01Z","abandoned":false}""";
        File.WriteAllText(Path.Combine(directory, $"{jobId}.json"), json);
    }

    private static string[] ListBranches(string dir)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("branch");
        startInfo.ArgumentList.Add("--format=%(refname:short)");

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git failed to start");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }

    [Fact]
    public async Task BareDoctorNeverPrintsProbeSectionsAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("auth:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("os:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeAddsAuthOsAndMcpSectionsAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("auth:", stdout, StringComparison.Ordinal);
        Assert.Contains("os:", stdout, StringComparison.Ordinal);
        Assert.Contains("mcp:", stdout, StringComparison.Ordinal);
        Assert.Contains(".mcp.json:", stdout, StringComparison.Ordinal);
        Assert.Contains("opencode.json:", stdout, StringComparison.Ordinal);

        // The harness's own money rule (CLAUSTRUM_SKIP_PROBE=1, set on every spawn by RunAsync above):
        // the banner and every backend's own probe line must say so, never make the real call.
        Assert.Contains("probe: skipped for every backend (CLAUSTRUM_SKIP_PROBE set; no paid request made)", stdout, StringComparison.Ordinal);
        Assert.Contains("probe:   skipped (CLAUSTRUM_SKIP_PROBE set)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeReportsNoMcpFileOnAFreshRepoAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".mcp.json:        not present", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeDetectsAnExistingMcpRegistrationAsync()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/
            """{"mcpServers":{"claustrum":{"command":"claustrum","args":["mcp"]}}}""");

        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".mcp.json:        registers claustrum", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProbeToleratesJsoncInVsCodeMcpFileAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".vscode"));
        File.WriteAllText(Path.Combine(cwd, ".vscode", "mcp.json"), /*lang=json*/ """
            {
              // hand-edited
              "servers": { "claustrum": { "type": "stdio", "command": "claustrum", "args": ["mcp"] }, },
            }
            """);

        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains(".vscode/mcp.json: registers claustrum", stdout, StringComparison.Ordinal);
    }

    // Issue #17: cursor's prompt-only deny list prints as its own `advisory:` line, never folded into
    // `problem:` (which would make ProbeLineAsync below skip the paid probe for a note that blocks
    // nothing).
    [Fact]
    public async Task DoctorCursorPrintsExactlyOneAdvisoryLineAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "cursor");

        Assert.Equal(Ok, exitCode);
        string[] advisoryLines = [.. stdout.Split('\n').Where(line => line.StartsWith("  advisory: ", StringComparison.Ordinal))];
        Assert.Single(advisoryLines);
        Assert.Equal(
            """  advisory: deny list is enforced by prompt only: cursor-agent has no native deny flag (NOTES.md "The cursor backend, validated against a real install", box 11)""",
            advisoryLines[0].TrimEnd('\r'));
    }

    [Fact]
    public async Task DoctorClaudePrintsNoAdvisoryLineAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "claude");

        Assert.Equal(Ok, exitCode);
        Assert.DoesNotContain("advisory:", stdout, StringComparison.Ordinal);
    }

    // Forces `found: false` (and therefore a `problem:` line) by handing the child process a PATH
    // that resolves to nothing — a `backends.cursor.path` override pointed at a missing file falls
    // back to a normal PATH search (BinaryLocator.ResolveCandidate), so on a machine with a real
    // cursor-agent installed (this dev box included) that override alone would not reproduce
    // `found: false`. An emptied PATH is deterministic regardless of what is actually installed.
    [Fact]
    public async Task DoctorProblemLinesPrecedeAdvisoryLinesForCursorAsync()
    {
        string emptyPathDir = Directory.CreateTempSubdirectory("claustrum-empty-path-").FullName;
        try
        {
            (int exitCode, string stdout, _) = await RunAsync(
                new Dictionary<string, string> { ["PATH"] = emptyPathDir },
                "backends", "doctor", "cursor");

            Assert.Equal(Ok, exitCode);
            Assert.Contains("  found:   False", stdout, StringComparison.Ordinal);
            int problemIndex = stdout.IndexOf("  problem: ", StringComparison.Ordinal);
            int advisoryIndex = stdout.IndexOf("  advisory: ", StringComparison.Ordinal);
            Assert.True(problemIndex >= 0, "expected a problem: line when cursor-agent is not on PATH");
            Assert.True(advisoryIndex > problemIndex, "expected advisory: to come after problem:");
        }
        finally
        {
            Directory.Delete(emptyPathDir, recursive: true);
        }
    }

    // Regression guard (issue #17): the advisory must never become ProbeLineAsync's skip reason —
    // with CLAUSTRUM_SKIP_PROBE=1 (RunAsync's default), the skip reason is always the env var, never
    // "skipped (deny list is enforced ...)".
    [Fact]
    public async Task DoctorProbeCursorSkipsOnTheEnvVarNotTheAdvisoryAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("backends", "doctor", "cursor", "--probe");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("  probe:   skipped (CLAUSTRUM_SKIP_PROBE set)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitScaffoldsClaustrumDirectoryAndClaudeSyncOnAFreshRepoAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "casts")));
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "briefs")));
        Assert.True(Directory.Exists(Path.Combine(cwd, ".claustrum", "worktrees")));
        Assert.True(File.Exists(Path.Combine(cwd, "claustrum.json")));
        Assert.Contains("cheap-coding", File.ReadAllText(Path.Combine(cwd, "claustrum.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".claude", "agents", "builder.md")));
        Assert.Contains(".claustrum/worktrees/", File.ReadAllText(Path.Combine(cwd, ".gitignore")), StringComparison.Ordinal);
        Assert.Contains("claude:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitDetectsGithubDirectoryAndAlsoSyncsCopilotAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".github"));

        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("copilot:", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "agents", "builder.agent.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".github", "skills", "claustrum", "SKILL.md")));
    }

    [Fact]
    public async Task InitWithAllSyncsEveryHarnessRegardlessOfDetectionAsync()
    {
        (int exitCode, string stdout, _) = await RunAsync("init", "--all");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("claude:", stdout, StringComparison.Ordinal);
        Assert.Contains("opencode:", stdout, StringComparison.Ordinal);
        Assert.Contains("copilot:", stdout, StringComparison.Ordinal);
        Assert.Contains("cursor:", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".opencode", "agent", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "agents", "builder.md")));
    }

    // InitCommand.DetectHarnesses: a repo with a .cursor/ directory already gets cursor synced even
    // without --all, the same detection InitDetectsGithubDirectoryAndAlsoSyncsCopilotAsync proves for
    // .github/ (issue #25).
    [Fact]
    public async Task InitDetectsCursorDirectoryAndAlsoSyncsCursorAsync()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".cursor"));

        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.Contains("cursor:", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "agents", "builder.md")));
        Assert.True(File.Exists(Path.Combine(cwd, ".cursor", "skills", "claustrum", "SKILL.md")));
    }

    [Fact]
    public async Task InitAppendsAPointerToAnExistingAgentsMdButNeverCreatesOneAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "# House rules\n");

        (int exitCode, _, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        string agentsMd = File.ReadAllText(Path.Combine(cwd, "AGENTS.md"));
        Assert.Contains("# House rules", agentsMd, StringComparison.Ordinal);
        Assert.Contains("Claustrum delegation", agentsMd, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(cwd, "CLAUDE.md")));
    }

    [Fact]
    public async Task InitNeverCreatesAnAgentsMdThatDidNotExistAsync()
    {
        (int exitCode, _, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.False(File.Exists(Path.Combine(cwd, "AGENTS.md")));
    }

    // "15 skipped" was pinned to the library's role count at the time (4 roles * 3 files + skill + 2
    // mcp configs); the architect role (M4) moved that number, and pinning a fresh literal again
    // would only repeat the same fragility on the next role addition. Reading the first run's own
    // "written" count and expecting it back as the second run's "skipped" count is exact without
    // depending on how many roles the library ships.
    [Fact]
    public async Task InitIsIdempotentOnRerunAsync()
    {
        (int firstExit, string firstStdout, _) = await RunAsync("init");
        Assert.Equal(Ok, firstExit);
        int writtenFirstRun = int.Parse(ClaudeSyncLinePattern().Match(firstStdout).Groups[1].Value, CultureInfo.InvariantCulture);
        string configBefore = File.ReadAllText(Path.Combine(cwd, "claustrum.json"));

        (int exitCode, string stdout, _) = await RunAsync("init");

        Assert.Equal(Ok, exitCode);
        Assert.Equal(configBefore, File.ReadAllText(Path.Combine(cwd, "claustrum.json")));
        Assert.Contains("already present", stdout, StringComparison.Ordinal);
        Assert.Contains($"claude: 0 written, {writtenFirstRun} skipped", stdout, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"claude: (\d+) written")]
    private static partial Regex ClaudeSyncLinePattern();

    [Fact]
    public async Task InitDoesNotDuplicateTheAgentsMdPointerOnRerunAsync()
    {
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "# House rules\n");
        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        Assert.Equal(Ok, (await RunAsync("init")).ExitCode);

        string agentsMd = File.ReadAllText(Path.Combine(cwd, "AGENTS.md"));
        int occurrences = agentsMd.Split("## Claustrum delegation").Length - 1;
        Assert.Equal(1, occurrences);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) =>
        RunAsync(extraEnv: null, args);

    // extraEnv is how a test puts CLAUSTRUM_PARENT_JOB (§D4's tree budget) on the child's own
    // environment — the same env the child's own backend spawn would inherit it from in the real
    // host-architect topology (NOTES.md "Tree budget accounting is a file ledger").
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(IReadOnlyDictionary<string, string>? extraEnv, params string[] args)
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        // §12's real paid call is a house rule violation waiting to happen the moment this suite runs
        // on a machine with a backend logged in — every spawn here skips it unless a test overrides it.
        startInfo.Environment["CLAUSTRUM_SKIP_PROBE"] = "1";
        foreach ((string key, string value) in extraEnv ?? new Dictionary<string, string>())
            startInfo.Environment[key] = value;

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}
