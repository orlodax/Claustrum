using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// What the role is handed and what the operator is shown. #68: the claude brief rides on stdin, never argv,
// `--resume` included. #62: an isolated run's brief ends with the isolation trailer (then the report trailer)
// and its argv denies `git checkout` / `git switch`; an in-place run gets neither. Human-mode `run` prints
// the branch, worktree and commit, and a refused commit's warning on stderr. The fake claude records its own
// argv and stdin (POSIX shells only), so these assert what the real binary was actually given.
public sealed class IsolatedRunBriefEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private string[] Argv() => File.ReadAllLines(repo.ArgvLog);

    private string Stdin() => File.ReadAllText(repo.StdinLog);

    private static string ValueOf(string[] argv, string flag) => argv[Array.IndexOf(argv, flag) + 1];

    private FakeClaudeScript Recording() => new() { ArgvLog = repo.ArgvLog, StdinLog = repo.StdinLog };

    [Fact]
    public async Task AnIsolatedRunsBriefIsOnStdinWithTheTrailersAndItsArgvDeniesCheckoutAndSwitchAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: 2);
        repo.Script(Recording());

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string[] argv = Argv();
        string denied = ValueOf(argv, "--disallowedTools");
        Assert.Equal(
            "Bash(git push),Bash(git push *),Bash(git checkout),Bash(git checkout *),Bash(git switch),Bash(git switch *)",
            denied);
        Assert.DoesNotContain(argv, line => line.Contains("do the task", StringComparison.Ordinal));
        string stdin = Stdin();
        Assert.StartsWith("do the task\n\n---\nYou are working in an isolated git worktree at ", stdin, StringComparison.Ordinal);
        Assert.Contains($"`{Str(result, "worktree")}`, on branch `{Str(result, "branch")}`", stdin, StringComparison.Ordinal);
        Assert.Contains($"The repository's main checkout is `{repo.Repo}`: never cd into it", stdin, StringComparison.Ordinal);
        Assert.Contains("`git checkout` and `git switch` are denied for this whole run", stdin, StringComparison.Ordinal);
        Assert.EndsWith("a reply without it is rejected.", stdin, StringComparison.Ordinal);
        Assert.True(
            stdin.IndexOf("isolated git worktree", StringComparison.Ordinal) < stdin.IndexOf("claustrum-report", StringComparison.Ordinal),
            "the report trailer must stay last");
    }

    // A numeric cap of 1 gates but does not isolate; null neither. Both run in the operator's own tree, so
    // there is no worktree for the trailer to name and no extra deny — the in-place builder keeps `git checkout`.
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task AnInPlaceRunHasNeitherTheIsolationTrailerNorTheCheckoutDenyAsync(int? maxParallel)
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel);
        repo.Script(Recording());

        (CliResult process, JsonElement result) = await repo.RunBuilderAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("worktree").ValueKind);
        string[] argv = Argv();
        Assert.Equal("Bash(git push),Bash(git push *)", ValueOf(argv, "--disallowedTools"));
        Assert.DoesNotContain(argv, line => line.Contains("git checkout", StringComparison.Ordinal));
        string stdin = Stdin();
        Assert.StartsWith("do the task\n\n---\nFinish your reply with the mandatory ```claustrum-report", stdin, StringComparison.Ordinal);
        Assert.DoesNotContain("isolated git worktree", stdin, StringComparison.Ordinal);
    }

    // #68, measured live: `claude -p` reads its prompt from stdin when argv carries none, and so does a
    // resumed run. The session id stays on argv; the brief does not.
    [Fact]
    public async Task AResumedRunKeepsItsSessionOnArgvAndItsBriefOnStdinAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: null);
        repo.Script(Recording());

        (CliResult process, _) = await repo.RunBuilderAsync("--resume", "sess-1234");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string[] argv = Argv();
        Assert.Equal("sess-1234", ValueOf(argv, "--resume"));
        Assert.DoesNotContain("--no-session-persistence", argv);
        Assert.DoesNotContain(argv, line => line.Contains("do the task", StringComparison.Ordinal));
        Assert.StartsWith("do the task", Stdin(), StringComparison.Ordinal);
    }

    // The brief that a `pgrep -f` pattern quoted (measured 2026-10-08: it matched the job running it) and the
    // one that can outgrow a Windows command line is verbatim on stdin, byte for byte.
    [Fact]
    public async Task ALongBriefWithShellMetacharactersReachesStdinVerbatimAndNeverArgvAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.WriteCast(maxParallel: null);
        repo.Script(Recording());
        string brief = string.Concat(Enumerable.Repeat("quote \" backtick ` dollar $HOME semi ; pipe | amp & €\n", 3000));
        File.WriteAllText(Path.Combine(repo.Repo, "long-brief.md"), brief);

        CliResult process = await repo.RunAsync("run", "builder", "--brief-file", "long-brief.md", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.StartsWith(brief, Stdin(), StringComparison.Ordinal);
        Assert.True(Argv().Sum(line => line.Length) < 2000, "argv must not grow with the brief");
    }

    [Fact]
    public async Task HumanModePrintsTheBranchWorktreeAndCommitOfAnIsolatedRunAsync()
    {
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("made.txt", "made")] });

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string[] lines = process.Stdout.Split('\n', StringSplitOptions.TrimEntries);
        string branch = lines.Single(line => line.StartsWith("branch: ", StringComparison.Ordinal))["branch: ".Length..];
        Assert.StartsWith("claustrum/", branch, StringComparison.Ordinal);
        Assert.Equal("status: Success (exit 0)", lines[0]);
        Assert.Single(lines, line => line.StartsWith("worktree: ", StringComparison.Ordinal) && line.EndsWith(IsolatedRepo.WorktreeTail(branch["claustrum/".Length..]), StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"commit: {TestGit.RevParse(repo.Repo, $"refs/heads/{branch}")}", lines);
        Assert.Contains("changed files:", lines);
        Assert.Contains("A made.txt", lines.Select(line => line.Trim()));
        Assert.DoesNotContain("warning:", process.Stderr, StringComparison.Ordinal);
    }

    // A refused commit is a warning, not a failure — and used to be visible only under --json.
    [Fact]
    public async Task HumanModePrintsARefusedCommitsWarningOnStderrAndNoCommitLineAsync()
    {
        repo.WriteCast(maxParallel: 2);
        repo.Script(new FakeClaudeScript { Writes = [("made.txt", "made")] });
        TestGit.WriteHook(repo.Repo, "pre-commit", "echo 'hook says no' >&2\nexit 1");

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        string branch = process.Stdout.Split('\n').Single(line => line.StartsWith("branch: ", StringComparison.Ordinal))["branch: ".Length..].Trim();
        Assert.Contains($"warning: work left uncommitted on {branch}: hook says no", process.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("commit:", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", process.Stdout, StringComparison.Ordinal);
        Assert.Contains("worktree: ", process.Stdout, StringComparison.Ordinal);
        Assert.Equal(TestGit.RevParse(repo.Repo, "main"), TestGit.RevParse(repo.Repo, $"refs/heads/{branch}"));
    }

    [Fact]
    public async Task HumanModeOfAnInPlaceRunPrintsNoBranchWorktreeOrCommitAsync()
    {
        repo.WriteCast(maxParallel: null);
        repo.Script(new FakeClaudeScript { Writes = [("made.txt", "made")] });

        CliResult process = await repo.RunAsync("run", "builder", "--brief", "do the task");

        Assert.Equal(ExitCodes.Ok, process.ExitCode);
        Assert.StartsWith("status: Success (exit 0)", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("branch:", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("worktree:", process.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("commit:", process.Stdout, StringComparison.Ordinal);
        Assert.Equal("?? made.txt", TestGit.Status(repo.Repo));
    }
}
