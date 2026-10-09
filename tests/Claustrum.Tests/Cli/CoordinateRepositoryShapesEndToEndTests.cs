using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #74 against the shapes a real operator's repository has that a tidy test fixture does not: a path with a space and
// non-ASCII characters (which the appendix must quote and git must be handed whole), a LINKED worktree as the cwd (its `.git`
// is a file, and the architect's worktree nests inside another worktree), a detached HEAD, and a repository with no commit.
// Fake `claude` throughout (IsolatedRepo.ForCoordinate); nothing reaches a real backend.
public sealed class CoordinateRepositoryShapesEndToEndTests : IDisposable
{
    private readonly List<IDisposable> disposables = [];

    public void Dispose()
    {
        foreach (IDisposable disposable in disposables)
            disposable.Dispose();
    }

    private IsolatedRepo NewRepo(string directoryPrefix = "claustrum-cli-")
    {
        IsolatedRepo repo = IsolatedRepo.ForCoordinate(directoryPrefix: directoryPrefix);
        disposables.Add(repo);
        return repo;
    }

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    [Fact]
    public async Task ARepositoryWhosePathHoldsASpaceAndNonAsciiCharactersRunsIsolatedAndIsCleanedUpAsync()
    {
        // The fake backend's cmd.exe script with such a working directory is unverified, so only the POSIX one runs here.
        FakeClaude.RequirePosixShell();
        IsolatedRepo repo = NewRepo("claustrum cli ünï-");
        Assert.Contains(' ', repo.Repo);
        repo.Script(new FakeClaudeScript { Writes = [("by-the-architect.txt", "x")], Summary = "wrote it" });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string jobId = Str(result, "job_id");
        string worktree = repo.WorktreePath(jobId);
        Assert.EndsWith(IsolatedRepo.WorktreeTail(jobId), Str(result, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("by-the-architect.txt", repo.Git("show", "--name-only", "--format=", Str(result, "commit")));

        // Quoted wherever it is handed to a child, so the path stays one argument.
        string systemMd = File.ReadAllText(Path.Combine(repo.JobsRoot, jobId, "system.md"));
        Assert.Contains($"--json --cwd \"{worktree}\"", systemMd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"run `claustrum jobs clean --cwd \"{worktree}\"`", systemMd, StringComparison.OrdinalIgnoreCase);

        CliResult clean = await repo.RunAsync("jobs", "clean");
        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task ALinkedWorktreeOfTheOperatorsRepositoryIsAValidCwdAndTheArchitectNestsInsideItAsync()
    {
        IsolatedRepo repo = NewRepo();
        string linked = Path.Combine(Directory.CreateTempSubdirectory("claustrum-linked-").FullName, "linked");
        repo.Git("worktree", "add", "-q", linked, "-b", "operator-feature");
        repo.Script(new FakeClaudeScript { Writes = [("by-the-architect.txt", "x")] });
        try
        {
            Assert.True(File.Exists(Path.Combine(linked, ".git")), "the premise: a linked worktree's .git is a file");

            CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task", "--json", "--cwd", linked);

            Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            string jobId = Str(document.RootElement, "job_id");
            Assert.Equal(Path.Combine(linked, ".claustrum", "worktrees", jobId), Str(document.RootElement, "worktree"));
            Assert.Equal($"claustrum/{jobId}", Str(document.RootElement, "branch"));
            Assert.Equal("by-the-architect.txt", repo.Git("show", "--name-only", "--format=", Str(document.RootElement, "commit")));
            // Neither checkout moved.
            Assert.Equal("main", repo.Git("branch", "--show-current"));
            Assert.Equal("operator-feature", TestGit.Run(linked, "branch", "--show-current"));
            Assert.Equal("", TestGit.Status(linked));
        }
        finally
        {
            TempTree.Delete(Path.GetDirectoryName(linked) ?? linked);
        }
    }

    [Fact]
    public async Task ADetachedHeadCheckoutRunsIsolatedAndStaysDetachedWhereItWasAsync()
    {
        IsolatedRepo repo = NewRepo();
        repo.Git("checkout", "-q", "--detach");
        string head = repo.Git("rev-parse", "HEAD");
        repo.Script(new FakeClaudeScript { Writes = [("by-the-architect.txt", "x")] });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal("", repo.Git("branch", "--show-current"));
        Assert.Equal(head, repo.Git("rev-parse", $"{Str(result, "commit")}~1"));
    }

    // No commit at all: the cast and the config can only be staged, never committed, so the precondition refuses before a
    // job exists — rather than the worktree add dying with "no commit to branch from" after the mint.
    [Fact]
    public async Task AnUnbornBranchIsRefusedForItsStagedFilesBeforeAnyJobExistsAsync()
    {
        IsolatedRepo repo = NewRepo();
        repo.Git("symbolic-ref", "HEAD", "refs/heads/unborn");
        Assert.StartsWith("A  ", repo.Git("status", "--porcelain").Split('\n')[0], StringComparison.Ordinal);

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task");

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains("which sees only committed files — commit (or un-ignore) ", process.Stderr, StringComparison.Ordinal);
        Assert.Contains("(uncommitted changes)", process.Stderr, StringComparison.Ordinal);
        Assert.Empty(repo.JobDirectories());
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
    }
}
