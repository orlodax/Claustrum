using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #124 stage 3b/3 review F1-F2: `coordinate --resume <job>` continues an architect that stopped with needs_input, in its own worktree and under the
// same cast and budget tree, once. A fake `claude` stands in for the architect (IsolatedRepo.ForCoordinate); nothing reaches a real backend.
public sealed class CoordinateResumeEndToEndTests : IDisposable
{
    private const string Asks =
        /*lang=json,strict*/ """{"status":"needs_input","summary":"which branch?","questions":[{"key":"branch","prompt":"Integrate where?","options":[],"allow_free_form":true}]}""";

    private const string Session = "architect-session";

    private readonly IsolatedRepo repo;

    public CoordinateResumeEndToEndTests()
    {
        FakeClaude.RequirePosixShell();
        repo = IsolatedRepo.ForCoordinate(budgetUsd: 5.00m);
    }

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private async Task<string> AskedAsync()
    {
        repo.Script(new FakeClaudeScript { ReportJson = Asks, SessionId = Session, Writes = [("architect-notes.txt", "started")] });
        (CliResult process, JsonElement? result) = await repo.CoordinateAsync("--no-live");
        Assert.Equal(ExitCodes.NeedsInput, process.ExitCode);
        Assert.Equal("needs_input", Str(result ?? default, "status"));
        return Str(result ?? default, "job_id");
    }

    private FakeClaudeScript Finishes() => new() { Summary = "integrated", SessionId = Session, ArgvLog = repo.ArgvLog, StdinLog = repo.StdinLog };

    [Fact]
    public async Task AResumedArchitectContinuesItsSessionInItsOwnWorktreeOnItsBranchAndLinksToTheFirstJobAsync()
    {
        string id = await AskedAsync();
        repo.Script(Finishes());

        (CliResult process, JsonElement? result) = await repo.CoordinateResumeAsync(id, "--brief", "integrate on main");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        JsonElement receipt = result ?? default;
        string next = Str(receipt, "job_id");
        Assert.NotEqual(id, next);
        Assert.Equal("success", Str(receipt, "status"));
        Assert.EndsWith(IsolatedRepo.WorktreeTail(id), Str(receipt, "worktree"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"claustrum/{id}", Str(receipt, "branch"));
        string[] argv = File.ReadAllLines(repo.ArgvLog);
        Assert.Equal(Session, argv[Array.IndexOf(argv, "--resume") + 1]);
        Assert.Contains("integrate on main", File.ReadAllText(repo.StdinLog), StringComparison.Ordinal);

        using JsonDocument request = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo.JobsRoot, next, "request.json")));
        Assert.Equal(id, Str(request.RootElement, "parent_job_id"));
        Assert.Equal(repo.WorktreePath(id), Str(request.RootElement, "cwd"));
        Assert.False(Directory.Exists(repo.WorktreePath(next)), "no worktree of its own: the first one is the architect's home");

        // The detached hosts of the tree watch the architect that runs: the first job's marker names its successor.
        Assert.Equal(next, File.ReadAllText(Path.Combine(repo.JobsRoot, id, "successor")).Trim());
    }

    [Fact]
    public async Task AResumedArchitectIsAccountedInTheSameBudgetTreeAsTheFirstOneAsync()
    {
        string id = await AskedAsync();
        repo.Script(Finishes());
        (_, JsonElement? result) = await repo.CoordinateResumeAsync(id, "--brief", "integrate on main");
        string next = Str(result ?? default, "job_id");

        CliResult budget = await repo.RunAsync("jobs", "budget", id);

        Assert.Equal(ExitCodes.Ok, budget.ExitCode);
        Assert.Contains($"{id}  architect", budget.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{next}  architect", budget.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeWithAnAnswersFileGivesTheArchitectTheAnswersAsync()
    {
        string id = await AskedAsync();
        repo.Script(Finishes());
        string answers = Path.Combine(repo.Repo, "answers.json");
        File.WriteAllText(answers, /*lang=json,strict*/ """{"branch":"release/2"}""");

        (CliResult process, _) = await repo.CoordinateResumeAsync(id, "--answers", answers);

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        string brief = File.ReadAllText(repo.StdinLog);
        Assert.Contains("release/2", brief, StringComparison.Ordinal);
        Assert.Contains("Integrate where?", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchitectIsResumedOnceAndTheSecondResumeStartsNothingAsync()
    {
        string id = await AskedAsync();
        repo.Script(Finishes());
        (CliResult first, _) = await repo.CoordinateResumeAsync(id, "--brief", "go");
        string[] before = repo.JobDirectories();

        (CliResult second, _) = await repo.CoordinateResumeAsync(id, "--brief", "go again");

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.Equal(ExitCodes.Usage, second.ExitCode);
        Assert.Contains("already answered", second.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, repo.JobDirectories());
    }

    [Fact]
    public async Task ResumeRefusesAJobThatIsNotACoordinateJobAndJobsAnswerRefusesACoordinateJobAsync()
    {
        string id = await AskedAsync();

        CliResult answer = await repo.RunAsync("jobs", "answer", id, "text");

        Assert.Equal(ExitCodes.Usage, answer.ExitCode);
        Assert.Contains($"claustrum coordinate --resume {id}", answer.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--cast", "default")]
    [InlineData("--issues", "5")]
    [InlineData("--cwd", ".")]
    public async Task ResumeRefusesTheFlagsOfTheOriginalJobAsync(string flag, string value)
    {
        string id = await AskedAsync();

        (CliResult process, _) = await repo.CoordinateResumeAsync(id, "--brief", "go", flag, value);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(flag, process.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.JobsRoot, id, "answered.json")));
    }
}
