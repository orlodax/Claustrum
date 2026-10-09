using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #74 trap 2: an isolated architect's worktree holds its children's under its own `.claustrum/worktrees/`, ignored by
// its status — and a plain `git worktree remove` of it exits 0 while deleting those children, an uncommitted file
// included (measured 2026-10-09, git 2.56). So `jobs clean` refuses a worktree that still holds job worktrees of
// its own, naming the `--cwd` sweep that clears them first, and removes a `probe-*` directory a killed `coordinate`
// left. The architect here is the real `coordinate` against a fake `claude` whose script cuts a child worktree
// itself, exactly as a delegated isolated builder's `claustrum run` would.
public sealed class JobsCleanArchitectWorktreeEndToEndTests : IDisposable
{
    private const string Child = "20261009-000000-childjob";

    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private async Task<string> CoordinateWithAChildWorktreeAsync(params string[] more)
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Commands = [$"git worktree add -q .claustrum/worktrees/{Child} -b claustrum/{Child}", .. more] });

        (CliResult process, JsonElement? result) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        JsonElement receipt = result ?? throw new InvalidOperationException(process.Stdout);
        // The nested worktree is the architect's own machinery: ignored, never committed, no warning about it.
        Assert.Empty(receipt.GetProperty("warnings").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("commit").ValueKind);
        return Str(receipt, "job_id");
    }

    private string Nested(string architectId) => Path.Combine(repo.WorktreePath(architectId), ".claustrum", "worktrees", Child);

    [Fact]
    public async Task APlainCleanRefusesTheArchitectsWorktreeNamingTheCwdSweepAndKeepsTheChildAsync()
    {
        string architect = await CoordinateWithAChildWorktreeAsync();
        File.WriteAllText(Path.Combine(Nested(architect), "builder-wip.txt"), "uncommitted work of a child\n");

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.BackendFailure, clean.ExitCode);
        Assert.Contains($"could not remove {architect}: ", clean.Stderr, StringComparison.Ordinal);
        Assert.Contains(
            $"it holds job worktrees of its own under {Path.Combine(repo.WorktreePath(architect), ".claustrum", "worktrees")} — run `claustrum jobs clean --cwd \"{repo.WorktreePath(architect)}\"` first",
            clean.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(repo.WorktreePath(architect)));
        Assert.Equal("uncommitted work of a child\n", File.ReadAllText(Path.Combine(Nested(architect), "builder-wip.txt")));
    }

    // The order of NOTES.md 2, end to end with the built binary: the child first (refused while it holds work, removed
    // once clean), then the architect's.
    [Fact]
    public async Task TheCwdSweepRemovesTheCleanChildThenThePlainCleanRemovesTheArchitectAsync()
    {
        string architect = await CoordinateWithAChildWorktreeAsync();
        string wip = Path.Combine(Nested(architect), "builder-wip.txt");
        File.WriteAllText(wip, "uncommitted\n");

        CliResult dirty = await repo.RunAsync("jobs", "clean", "--cwd", repo.WorktreePath(architect));

        Assert.Equal(ExitCodes.BackendFailure, dirty.ExitCode);
        Assert.Contains($"could not remove {Child}: ", dirty.Stderr, StringComparison.Ordinal);
        Assert.Contains("uncommitted changes: ?? builder-wip.txt", dirty.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(wip));

        File.Delete(wip);
        CliResult swept = await repo.RunAsync("jobs", "clean", "--cwd", repo.WorktreePath(architect));

        Assert.Equal(ExitCodes.Ok, swept.ExitCode);
        Assert.Contains($"removed {Child} (branch claustrum/{Child} kept)", swept.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Nested(architect)));
        Assert.Contains($"claustrum/{Child}", TestGit.Branches(repo.Repo));

        CliResult final = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, final.ExitCode);
        Assert.Contains($"removed {architect} (branch claustrum/{architect} kept)", final.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.WorktreePath(architect)));
        Assert.Contains($"claustrum/{architect}", TestGit.Branches(repo.Repo));
    }

    // The trap itself, as the refusal prevents it: removing the parent first deleted the child's uncommitted file.
    [Fact]
    public async Task TheChildsUncommittedFileSurvivesEveryCleanThatRefusesAsync()
    {
        string architect = await CoordinateWithAChildWorktreeAsync();
        string wip = Path.Combine(Nested(architect), "builder-wip.txt");
        File.WriteAllText(wip, "the only copy\n");

        await repo.RunAsync("jobs", "clean");
        await repo.RunAsync("jobs", "clean");
        await repo.RunAsync("jobs", "clean", "--cwd", repo.WorktreePath(architect));

        Assert.Equal("the only copy\n", File.ReadAllText(wip));
        string[] registered = [.. TestGit.WorktreePaths(repo.Repo).Select(path => Path.GetFileName(path))];
        Assert.Contains(architect, registered);
        Assert.Contains(Child, registered);
    }

    // An empty `.claustrum/worktrees/` — what every architect that delegated nothing, or whose children are all
    // swept, holds — is not a nested worktree.
    [Fact]
    public async Task AnEmptyNestedWorktreesDirectoryDoesNotBlockTheArchitectsRemovalAsync()
    {
        FakeClaude.RequirePosixShell();
        repo.Script(new FakeClaudeScript { Commands = ["mkdir -p .claustrum/worktrees"] });
        (CliResult process, JsonElement? result) = await repo.CoordinateAsync();
        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr);
        string architect = Str(result ?? throw new InvalidOperationException(process.Stdout), "job_id");

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Contains($"removed {architect}", clean.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.WorktreePath(architect)));
    }

    // A nested directory whose worktree registration is gone but whose `.git` file is still there is refused too: git
    // itself will not work in it, and a plain remove of the parent would delete files nothing registered says exist.
    [Fact]
    public async Task ANestedDirectoryWithAGitFileButNoAdminDirectoryIsStillRefusedAsync()
    {
        string architect = await CoordinateWithAChildWorktreeAsync();
        string admin = Path.Combine(repo.Repo, ".git", "worktrees", Child);
        Assert.True(Directory.Exists(admin), admin);
        TempTree.Delete(admin);

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.BackendFailure, clean.ExitCode);
        Assert.Contains("it holds job worktrees of its own", clean.Stderr, StringComparison.Ordinal);
        Assert.True(Directory.Exists(repo.WorktreePath(architect)));
        Assert.True(File.Exists(Path.Combine(Nested(architect), ".git")));
    }

    // ---- G5: a probe a hard kill left behind -------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyStrayProbeDirectoryIsRemovedWithItsOwnLineAndExitZeroAsync()
    {
        string probe = Path.Combine(repo.Repo, ".claustrum", "worktrees", "probe-0badf00d");
        Directory.CreateDirectory(probe);

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Contains("removed stray probe directory probe-0badf00d", clean.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(probe));
    }

    [Fact]
    public async Task ANonEmptyProbeDirectoryIsLeftAndReportedNotAWorkingTreeAsync()
    {
        // A jobs root that exists, or no directory counts as finished (JobDirectory.IsFinished) and the sweep skips it.
        Directory.CreateDirectory(repo.JobsRoot);
        string probe = Path.Combine(repo.Repo, ".claustrum", "worktrees", "probe-0badf00d");
        Directory.CreateDirectory(probe);
        File.WriteAllText(Path.Combine(probe, "kept.txt"), "x");

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.Equal(ExitCodes.BackendFailure, clean.ExitCode);
        Assert.DoesNotContain("removed stray probe directory", clean.Stdout, StringComparison.Ordinal);
        Assert.Contains("could not remove probe-0badf00d: ", clean.Stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(probe, "kept.txt")));
    }

    [Fact]
    public async Task ARegisteredWorktreeNamedLikeAProbeThatWasEmptiedIsNotDeletedAsAStrayAsync()
    {
        Directory.CreateDirectory(repo.JobsRoot);
        string registered = Path.Combine(repo.Repo, ".claustrum", "worktrees", "probe-feedface");
        repo.Git("worktree", "add", "-q", registered, "-b", "claustrum/probe-feedface");
        TempTree.Delete(registered);
        Directory.CreateDirectory(registered);

        CliResult clean = await repo.RunAsync("jobs", "clean");

        Assert.DoesNotContain("removed stray probe directory", clean.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(registered));
        Assert.Contains("probe-feedface", TestGit.WorktreePaths(repo.Repo).Select(path => Path.GetFileName(path)));
    }
}
