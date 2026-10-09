using System.Text.Json;
using System.Text.RegularExpressions;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #74 through the real built binary, in a real repository with a fake `claude` (IsolatedRepo.ForCoordinate): in a
// git repository `coordinate` runs its architect in `.claustrum/worktrees/<job id>` on `claustrum/<job id>`, never
// in the operator's checkout, and commits what the architect leaves there; outside one it runs in place with a
// text that says there is no git. Nothing here reaches a real backend: the child's PATH is git's directory plus
// the system's, `claustrum.json` (committed at the git root, where Config.Load reads it) points the claude
// backend at the script, and CLAUSTRUM_HOME/HOME are scratch directories.
public sealed class CoordinateIsolatedEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private string JobFile(string jobId, string name) => File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, name));

    [Fact]
    public async Task TheArchitectRunsInItsOwnWorktreeOnItsOwnBranchAndWhatItWroteIsACommitThereAsync()
    {
        repo.Script(new FakeClaudeScript { Writes = [("architect-notes.txt", "written by the architect")], Summary = "integrated" });
        string headBefore = repo.Git("rev-parse", "HEAD");
        string reflogBefore = repo.Git("reflog", "show", "--format=%gs", "HEAD");

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string jobId = Str(result, "job_id");
        string branch = $"claustrum/{jobId}";
        Assert.Equal("architect", Str(result, "role"));
        Assert.Equal("success", Str(result, "status"));
        Assert.Equal(branch, Str(result, "branch"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(result, "worktree"), StringComparison.OrdinalIgnoreCase);

        // The commit is the branch tip, one commit past where the architect started, holding the file it wrote.
        string tip = repo.Git("rev-parse", $"refs/heads/{branch}");
        Assert.Equal(tip, Str(result, "commit"));
        Assert.Equal(1, TestGit.CommitCount(repo.Repo, $"main..{branch}"));
        Assert.Equal($"claustrum architect {jobId}\n\nintegrated", repo.Git("log", "-1", "--format=%B", tip));
        Assert.Equal("architect-notes.txt", repo.Git("show", "--name-only", "--format=", tip));
        Assert.Equal("", TestGit.Status(repo.WorktreePath(jobId)));

        // The receipt on disk says the same: `jobs show` reads the file.
        JsonElement onDisk = repo.ReadResult(jobId);
        Assert.Equal(branch, Str(onDisk, "branch"));
        Assert.Equal(tip, Str(onDisk, "commit"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(onDisk, "worktree"), StringComparison.OrdinalIgnoreCase);

        // The operator's checkout was never touched: same branch, same HEAD, no checkout in its reflog, clean.
        Assert.Equal("main", repo.Git("branch", "--show-current"));
        Assert.Equal(headBefore, repo.Git("rev-parse", "HEAD"));
        Assert.Equal(reflogBefore, repo.Git("reflog", "show", "--format=%gs", "HEAD"));
        Assert.DoesNotContain("checkout:", repo.Git("reflog", "show", "--format=%gs", "HEAD"), StringComparison.Ordinal);
        Assert.Equal("", TestGit.Status(repo.Repo));
        Assert.False(File.Exists(Path.Combine(repo.Repo, "architect-notes.txt")));
    }

    [Fact]
    public async Task TheSystemPromptNamesTheWorktreeEverywhereAndLeavesNoJobIdTokenAsync()
    {
        repo.Script(new FakeClaudeScript());

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string jobId = Str(parsed ?? throw new InvalidOperationException(process.Stdout), "job_id");
        string worktree = repo.WorktreePath(jobId);
        string systemMd = JobFile(jobId, "system.md");
        string coordination = systemMd[systemMd.IndexOf("## Coordination\n\nCast: ", StringComparison.Ordinal)..systemMd.IndexOf("\n## House rules\n", StringComparison.Ordinal)];

        Assert.Contains($"Work branch: claustrum/{jobId} — you are already on it, in your own worktree {worktree}", coordination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"--json --cwd \"{worktree}\"", coordination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"run `claustrum jobs clean --cwd \"{worktree}\"`", coordination, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Job tree: {jobId}", coordination, StringComparison.Ordinal);
        Assert.DoesNotContain("{{job_id}}", systemMd, StringComparison.Ordinal);
        Assert.DoesNotContain($"--cwd \"{repo.Repo}\"", coordination, StringComparison.Ordinal);
        Assert.DoesNotContain("No git repository", coordination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestJsonRunsInTheWorktreeAndItsBriefNamesItAsTheWorkingDirectoryAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { StdinLog = repo.StdinLog });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string jobId = Str(parsed ?? throw new InvalidOperationException(process.Stdout), "job_id");
        string worktree = repo.WorktreePath(jobId);
        using JsonDocument request = JsonDocument.Parse(JobFile(jobId, "request.json"));
        string requestText = JobFile(jobId, "request.json");

        Assert.Equal(worktree, request.RootElement.GetProperty("cwd").GetString());
        Assert.Equal(jobId, request.RootElement.GetProperty("env").GetProperty("CLAUSTRUM_PARENT_JOB").GetString());
        string recordedBrief = request.RootElement.GetProperty("brief").GetString() ?? "";
        Assert.Contains($"- Working directory: {worktree}", recordedBrief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{job_id}}", requestText, StringComparison.Ordinal);

        // What the backend was actually handed on stdin: the user prompt, with the real worktree on its one line.
        string brief = File.ReadAllText(repo.StdinLog);
        Assert.Contains($"- Working directory: {worktree}", brief, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{job_id}}", brief, StringComparison.Ordinal);
        Assert.Contains("do the task", brief, StringComparison.Ordinal);
    }

    // The human output of `run` for an isolated job is three lines after the receipt; `coordinate` prints the same three.
    [Fact]
    public async Task HumanOutputNamesTheBranchTheWorktreeAndTheCommitAsync()
    {
        repo.Script(new FakeClaudeScript { Writes = [("by-architect.txt", "x")], Summary = "done" });

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        // Windows prints CRLF, and a .NET `$` matches only before `\n`.
        string stdout = process.Stdout.ReplaceLineEndings("\n");
        string jobId = Regex.Match(stdout, @"^job:\s+(\S+)", RegexOptions.Multiline).Groups[1].Value;
        Assert.NotEmpty(jobId);
        Assert.Contains($"\nbranch: claustrum/{jobId}\n", stdout, StringComparison.Ordinal);
        Assert.Matches($@"(?m)^worktree: .*[/\\]\.claustrum[/\\]worktrees[/\\]{jobId}$", stdout);
        Assert.Contains($"\ncommit: {repo.Git("rev-parse", $"refs/heads/claustrum/{jobId}")}\n", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", process.Stderr, StringComparison.Ordinal);
    }

    // An architect that wrote nothing has no commit: the line is absent, not `commit: ` with nothing after it.
    [Fact]
    public async Task HumanOutputOmitsTheCommitLineWhenTheArchitectLeftNothingAsync()
    {
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Contains("\nbranch: claustrum/", process.Stdout, StringComparison.Ordinal);
        Assert.Contains("\nworktree: ", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("commit:", process.Stdout, StringComparison.Ordinal);
        string jobId = Regex.Match(process.Stdout, @"^job:\s+(\S+)", RegexOptions.Multiline).Groups[1].Value;
        Assert.Equal(repo.Git("rev-parse", "main"), repo.Git("rev-parse", $"refs/heads/claustrum/{jobId}"));
    }

    // F3: the architect's receipt can carry warnings (a refused commit of its leftovers), and `coordinate` printed
    // none of them. `run` writes them to stderr as `warning: …`, and so does this.
    [Fact]
    public async Task AWarningOnTheArchitectsReceiptIsPrintedOnStderrAsync()
    {
        FakeClaude.RequirePosixShell();
        TestGit.WriteHook(repo.Repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");
        repo.Script(new FakeClaudeScript { Writes = [("wanted.txt", "work")] });

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task");

        string jobId = Regex.Match(process.Stdout, @"^job:\s+(\S+)", RegexOptions.Multiline).Groups[1].Value;
        Assert.Contains($"warning: work left uncommitted on claustrum/{jobId}: hook says no", process.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("commit:", process.Stdout, StringComparison.Ordinal);
    }

    // No repository at all: nothing to cut a branch from, so the architect runs in place, as before #74 — with a
    // text that says there is no git, and no worktree, no branch, no commit on the receipt. The architect is on a
    // backend that does not exist, so no process of any kind is spawned (a plain directory's claustrum.json is not
    // read, and the real claude on a developer's PATH must never be reachable from here).
    [Fact]
    public async Task OutsideAGitRepositoryTheArchitectRunsInPlaceWithTheNoGitTextAsync()
    {
        using ClaustrumCli plain = new();
        Directory.CreateDirectory(plain.CastsDirectory);
        File.WriteAllText(Path.Combine(plain.CastsDirectory, "default.json"), /*lang=json,strict*/
            """{"name":"default","library":"1.0.0","architect":{"mode":"spawned","model":"nonexistent:x"},"roles":{},"budget_usd":null}""");

        CliResult process = await plain.RunAsync(["coordinate", "--brief", "do the task", "--json"], "", IsolatedRepo.ChildPath());

        Assert.Equal(ExitCodes.BackendMissing, process.ExitCode);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        JsonElement result = document.RootElement;
        string jobId = Str(result, "job_id");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        Assert.False(Directory.Exists(Path.Combine(plain.Cwd, ".claustrum", "worktrees")));

        // The appendix only: the architect role's own body, above it, is the host architect's text and talks about git.
        string systemMd = File.ReadAllText(Path.Combine(plain.JobsDirectory, jobId, "system.md"));
        string coordination = systemMd[systemMd.IndexOf("## Coordination\n\nCast: ", StringComparison.Ordinal)..systemMd.IndexOf("\n## House rules\n", StringComparison.Ordinal)];
        Assert.Contains($"No git repository at {plain.Cwd}: there is no work branch and no worktree isolation", coordination, StringComparison.Ordinal);
        Assert.DoesNotContain("Work branch", coordination, StringComparison.Ordinal);
        Assert.DoesNotContain("jobs clean", coordination, StringComparison.Ordinal);
        Assert.Contains($"--json --cwd \"{plain.Cwd}\"", coordination, StringComparison.Ordinal);
        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(Path.Combine(plain.JobsDirectory, jobId, "request.json")));
        Assert.Equal(plain.Cwd, request.RootElement.GetProperty("cwd").GetString());
    }

    // The architect is admitted into the budget tree of the shell it runs in BEFORE its worktree is cut (the admission
    // moved from Runner to DelegateEngine's isolated path with #74): one the ledger refuses costs no worktree and no branch.
    [Fact]
    public async Task AnArchitectTheTreeBudgetRefusesIsAdmittedBeforeItsWorktreeSoItLeavesNoBranchAndNoWorktreeAsync()
    {
        using IsolatedRepo capped = IsolatedRepo.ForCoordinate(budgetUsd: 0.02m);
        capped.Script(new FakeClaudeScript { Writes = [("a.txt", "x")] });
        Dictionary<string, string?> tree = new() { ["CLAUSTRUM_PARENT_JOB"] = "tree-admission" };

        CliResult first = await capped.RunWithEnvAsync(["coordinate", "--brief", "do", "--json"], tree);
        CliResult second = await capped.RunWithEnvAsync(["coordinate", "--brief", "do", "--json"], tree);
        string[] branches = TestGit.Branches(capped.Repo);
        string[] worktrees = TestGit.WorktreePaths(capped.Repo);
        CliResult refused = await capped.RunWithEnvAsync(["coordinate", "--brief", "do", "--json"], tree);

        Assert.True(first.ExitCode == ExitCodes.Ok, first.Stderr);
        Assert.True(second.ExitCode == ExitCodes.Ok, second.Stderr);
        Assert.Equal(ExitCodes.Budget, refused.ExitCode);
        using JsonDocument document = JsonDocument.Parse(refused.Stdout);
        JsonElement result = document.RootElement;
        Assert.Equal("budget_exceeded", Str(result, "status"));
        Assert.Contains("nothing left for role 'architect'", Str(result, "error"), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("branch").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("commit").ValueKind);
        Assert.Equal(branches, TestGit.Branches(capped.Repo));
        Assert.Equal(worktrees, TestGit.WorktreePaths(capped.Repo));
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(capped.Repo, ".claustrum", "worktrees")).Length);
    }

    // Two coordinate runs in one repository: each gets its own worktree and branch, and neither sees the other's file.
    [Fact]
    public async Task TwoArchitectsInOneRepositoryGetTheirOwnWorktreesAndBranchesAsync()
    {
        repo.Script(new FakeClaudeScript { WriteUniqueFile = true, SleepSeconds = 1, Summary = "wrote a unique file" });

        (CliResult Process, JsonElement? Result)[] runs = await Task.WhenAll(repo.CoordinateAsync(), repo.CoordinateAsync());

        foreach ((CliResult process, JsonElement? result) in runs)
            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);

        string[] branches = [.. runs.Select(run => Str(run.Result ?? throw new InvalidOperationException(run.Process.Stdout), "branch"))];
        Assert.Equal(2, branches.Distinct(StringComparer.Ordinal).Count());
        foreach (string branch in branches)
            Assert.Equal(1, TestGit.CommitCount(repo.Repo, $"main..{branch}"));

        Assert.Equal("main", repo.Git("branch", "--show-current"));
        Assert.Equal("", TestGit.Status(repo.Repo));
    }
}
