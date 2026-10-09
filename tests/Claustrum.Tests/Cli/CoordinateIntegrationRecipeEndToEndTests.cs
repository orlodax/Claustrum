using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// The integration recipe the `## Coordination` appendix hands an isolated architect (#74 F5), run for real: the fake
// "architect" delegates to a builder with the real `claustrum run builder --cwd <its worktree>` (the builder is the same
// fake script, which tells itself apart by how deep in `.claustrum/worktrees/` it is), commits on its own work branch AFTER
// the builder's worktree was cut, then integrates in the appendix's order — `git -C <builder worktree> rebase <work branch>`,
// `git merge --ff-only <builder branch>` from its own tree, `claustrum jobs clean --cwd <its worktree>` last — and the
// operator finally fast-forwards its own branch to the architect's. Nothing real can run: every process here is the built
// binary, git, or the fake script (PATH is git's directory plus the system's), and the script refuses to nest past depth 4.
public sealed class CoordinateIntegrationRecipeEndToEndTests : IDisposable
{
    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate(builderMaxParallel: 2);

    public void Dispose() => repo.Dispose();

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";

    private string FakeFile(string name) => Path.Combine(repo.Home, "fake", name);

    private static string Binary => Path.Combine(AppContext.BaseDirectory, "claustrum");

    [Fact]
    public async Task RebaseInsideTheBuildersWorktreeThenFastForwardThenCleanIntegratesWithoutAMergeCommitAsync()
    {
        FakeClaude.RequirePosixShell();
        string guard = FakeFile("depth");
        string architectSteps = string.Join('\n',
        [
            $"OUT=$('{Binary}' run builder --cast default --brief 'write child.txt' --json --cwd \"$PWD\")",
            $"printf '%s\\n' \"$OUT\" > '{FakeFile("builder-receipt.json")}'",
            "BR=$(printf '%s' \"$OUT\" | sed -n 's/.*\"branch\":\"\\([^\"]*\\)\".*/\\1/p')",
            "WT=$(printf '%s' \"$OUT\" | sed -n 's/.*\"worktree\":\"\\([^\"]*\\)\".*/\\1/p')",
            "printf 'architect\\n' > arch.txt",
            "git add arch.txt",
            "git commit -q -m 'architect work'",
            $"git -C \"$WT\" rebase \"$(git branch --show-current)\" > '{FakeFile("rebase.log")}' 2>&1",
            $"git merge --ff-only \"$BR\" > '{FakeFile("merge.log")}' 2>&1",
            $"'{Binary}' jobs clean --cwd \"$PWD\" > '{FakeFile("clean.log")}' 2>&1",
        ]);
        repo.Script(new FakeClaudeScript
        {
            Commands =
            [
                $"n=$(cat '{guard}' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '{guard}'; if [ $n -gt 4 ]; then echo 'nesting guard' >&2; exit 1; fi",
                "case \"$PWD\" in\n*/.claustrum/worktrees/*/.claustrum/worktrees/*) printf 'built\\n' > child.txt ;;\n*)\n" + architectSteps + "\n;;\nesac",
            ],
        });

        (CliResult process, JsonElement? parsed) = await repo.CoordinateAsync();

        Assert.True(process.ExitCode == ExitCodes.Ok, process.Stderr + process.Stdout);
        JsonElement result = parsed ?? throw new InvalidOperationException(process.Stdout);
        string architect = Str(result, "job_id");
        string workBranch = Str(result, "branch");
        Assert.Equal("2", File.ReadAllText(guard).Trim());

        // The builder ran for real, isolated, nested under the architect's worktree, cut from the work branch.
        using JsonDocument builder = JsonDocument.Parse(File.ReadAllText(FakeFile("builder-receipt.json")));
        string builderBranch = Str(builder.RootElement, "branch");
        string builderWorktree = Str(builder.RootElement, "worktree");
        Assert.Equal("success", Str(builder.RootElement, "status"));
        Assert.StartsWith(Path.Combine(repo.WorktreePath(architect), ".claustrum", "worktrees") + Path.DirectorySeparatorChar, builderWorktree, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"claustrum/{Str(builder.RootElement, "job_id")}", builderBranch);

        // Each step of the recipe worked: the rebase and the fast-forward said nothing fatal, the clean removed the child's worktree.
        Assert.DoesNotContain("fatal", File.ReadAllText(FakeFile("rebase.log")), StringComparison.Ordinal);
        Assert.DoesNotContain("fatal", File.ReadAllText(FakeFile("merge.log")), StringComparison.Ordinal);
        Assert.Contains($"removed {Str(builder.RootElement, "job_id")} (branch {builderBranch} kept)", File.ReadAllText(FakeFile("clean.log")), StringComparison.Ordinal);
        Assert.False(Directory.Exists(builderWorktree));
        Assert.Contains(builderBranch, TestGit.Branches(repo.Repo));

        // The work branch: the base, the architect's commit, then the builder's commit rebased onto it — linear, no merge commit.
        string[] subjects = [.. repo.Git("log", "--format=%s", $"main..{workBranch}").Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Equal(2, subjects.Length);
        Assert.Equal("architect work", subjects[1]);
        Assert.StartsWith("claustrum builder ", subjects[0], StringComparison.Ordinal);
        Assert.Equal("0", repo.Git("rev-list", "--merges", "--count", $"main..{workBranch}"));
        Assert.Equal(repo.Git("rev-parse", workBranch), Str(result, "commit"));
        Assert.Equal(["arch.txt", "child.txt"], [.. repo.Git("ls-tree", "--name-only", workBranch).Split('\n').Where(name => name.EndsWith(".txt", StringComparison.Ordinal) && name != "seed.txt")]);
        Assert.Equal("", TestGit.Status(repo.WorktreePath(architect)));

        // The operator integrates last: its branch fast-forwards while the architect's worktree still holds the work
        // branch (measured), and `jobs clean` then takes the architect's worktree off.
        repo.Git("merge", "--ff-only", workBranch);
        Assert.Equal(repo.Git("rev-parse", workBranch), repo.Git("rev-parse", "main"));
        CliResult clean = await repo.RunAsync("jobs", "clean");
        Assert.Equal(ExitCodes.Ok, clean.ExitCode);
        Assert.Contains($"removed {architect} (branch {workBranch} kept)", clean.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.WorktreePath(architect)));
    }
}
