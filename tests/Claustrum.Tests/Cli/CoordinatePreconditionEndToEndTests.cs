using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The CLI door of #74's precondition (ArchitectWorktreeTests has the matrix at the precondition's own level):
// whatever the architect's worktree would lack is refused with exit 2 and a message naming what to fix — before
// a job directory exists, before a worktree is cut, and before `gh` is asked for an issue. The pass cases run the
// fake claude to the end, so nothing here can reach a real backend (PATH is git's directory plus the system's).
public sealed class CoordinatePreconditionEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    [Theory]
    [MemberData(nameof(PreconditionCases.Names), MemberType = typeof(PreconditionCases))]
    public async Task EachRefusalExitsTwoNamingWhatToFixAndLeavesNoJobNoWorktreeAndAnUntouchedCheckoutAsync(string name)
    {
        PreconditionCase refusal = PreconditionCases.Apply(repo, name);
        string before = Snapshot();
        string[] arguments = ["coordinate", "--brief", "x", "--cwd", refusal.Cwd, .. refusal.CastName is { } cast ? (string[])["--cast", cast] : []];

        CliResult process = await repo.RunAsync(arguments);

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Contains(refusal.Fragment, process.Stderr, StringComparison.Ordinal);
        Assert.Equal("", process.Stdout);
        Assert.Empty(repo.JobDirectories());
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, ".claustrum", "worktrees")));
        Assert.Equal(before, Snapshot());
    }

    private string Snapshot() => string.Join('\n', repo.Git("rev-parse", "HEAD"), repo.Git("branch", "--list"), repo.Git("status", "--porcelain"));

    // --json is a contract with a parser: a refusal is an error on stderr and an empty stdout, never half a document.
    [Fact]
    public async Task ARefusalUnderJsonWritesNothingToStdoutAsync()
    {
        File.AppendAllText(Path.Combine(repo.Repo, "claustrum.json"), "\n");

        (CliResult process, JsonElement? result) = await repo.CoordinateAsync();

        Assert.Equal(ExitCodes.Usage, process.ExitCode);
        Assert.Null(result);
        Assert.Contains("commit (or un-ignore) claustrum.json (uncommitted changes) first", process.Stderr, StringComparison.Ordinal);
    }

    // G1: junk under the roles directory is nobody's input; an untracked note beside the role files is not either.
    [Fact]
    public async Task IgnoredJunkAndAnUntrackedNoteUnderRolesDoNotStopTheRunAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, ".gitignore"), IsolatedRepo.MachineryIgnoreRules + ".DS_Store\n*.swp\n");
        repo.Git("add", ".gitignore");
        repo.Git("commit", "-q", "-m", "ignore junk");
        string roles = Path.Combine(repo.Repo, ".claustrum", "roles", "tester");
        Directory.CreateDirectory(Path.Combine(roles, "parts"));
        File.WriteAllText(Path.Combine(roles, ".DS_Store"), "x");
        File.WriteAllText(Path.Combine(roles, ".ROLE.md.swp"), "x");
        File.WriteAllText(Path.Combine(roles, "parts", ".DS_Store"), "x");
        File.WriteAllText(Path.Combine(roles, "notes.txt"), "x");
        repo.Script(new FakeClaudeScript());

        (CliResult process, JsonElement? result) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal("success", result?.GetProperty("status").GetString());
        Assert.DoesNotContain("warning:", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredHarnessConfigRunsWithoutAWarningAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, ".gitignore"), IsolatedRepo.MachineryIgnoreRules + ".mcp.json\n");
        repo.Git("add", ".gitignore");
        repo.Git("commit", "-q", "-m", "ignore the local mcp config");
        File.WriteAllText(Path.Combine(repo.Repo, ".mcp.json"), "{}\n");
        repo.Script(new FakeClaudeScript());

        (CliResult process, JsonElement? result) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Equal("success", result?.GetProperty("status").GetString());
        Assert.DoesNotContain("warning:", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesOnlyInInfoExcludeAreEnoughAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, ".gitignore"), "");
        repo.Git("add", ".gitignore");
        repo.Git("commit", "-q", "-m", "no committed rules");
        File.WriteAllText(Path.Combine(repo.Repo, ".git", "info", "exclude"), IsolatedRepo.MachineryIgnoreRules);
        repo.Script(new FakeClaudeScript());

        (CliResult process, _) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
    }

    // G1: a committed harness config with a local edit is not a refusal — the architect's children get HEAD's copy —
    // and the warning goes to stderr before the run, so `--json`'s stdout stays one document.
    [Fact]
    public async Task ATrackedOpencodeJsonWithALocalEditRunsWithAWarningOnStderrAndOneJsonDocumentOnStdoutAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, "opencode.json"), "{}\n");
        repo.CommitAll("opencode config");
        File.WriteAllText(Path.Combine(repo.Repo, "opencode.json"), /*lang=json,strict*/ """{"mine":true}""" + "\n");
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task", "--json");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Contains("warning: opencode.json (uncommitted changes): the architect's worktree gets HEAD's copy — your local edit stays out of it", process.Stderr, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(process.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
        string jobId = document.RootElement.GetProperty("job_id").GetString() ?? "";
        Assert.Equal("{}", File.ReadAllText(Path.Combine(repo.WorktreePath(jobId), "opencode.json")).Trim());
    }

    [Fact]
    public async Task TheSameWarningIsPrintedBeforeTheReceiptInHumanModeAsync()
    {
        File.WriteAllText(Path.Combine(repo.Repo, "opencode.json"), "{}\n");
        repo.CommitAll("opencode config");
        File.AppendAllText(Path.Combine(repo.Repo, "opencode.json"), "\n");
        repo.Script(new FakeClaudeScript());

        CliResult process = await repo.RunAsync("coordinate", "--brief", "do the task");

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        Assert.Contains("warning: opencode.json (uncommitted changes): the architect's worktree gets HEAD's copy", process.Stderr, StringComparison.Ordinal);
        Assert.Contains("job:", process.Stdout, StringComparison.Ordinal);
    }

    // A cwd that is not a git repository at all has nothing to check: the precondition belongs to the worktree.
    [Fact]
    public async Task APlainDirectoryWithAnUntrackedEverythingIsNotRefusedAsync()
    {
        using ClaustrumCli plain = new();
        Directory.CreateDirectory(plain.CastsDirectory);
        File.WriteAllText(Path.Combine(plain.CastsDirectory, "default.json"), /*lang=json,strict*/
            """{"name":"default","library":"1.0.0","architect":{"mode":"spawned","model":"nonexistent:x"},"roles":{},"budget_usd":null}""");

        CliResult process = await plain.RunAsync(["coordinate", "--brief", "x"], "", IsolatedRepo.ChildPath());

        Assert.Equal(ExitCodes.BackendMissing, process.ExitCode);
        Assert.DoesNotContain("sees only committed files", process.Stderr, StringComparison.Ordinal);
    }
}
