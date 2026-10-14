using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// Review 2026-10-14, round 2: commits only the worktree's own HEAD reflog reaches die with its admin directory, so the free
// refuses them (the count and a sha in the message) unless a ref holds them or the branch carries the same patch (`git cherry`,
// which is why the documented rebase-then-fast-forward recipe still frees). A real repository and real git per case.
public sealed class JobWorktreeFreeReflogTests : IDisposable
{
    private readonly ScratchRoot scratch = new("claustrum-reflog-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Repo, JobWorktreeInfo Worktree)> StartAsync()
    {
        string repo = scratch.CreateSeededRepo();
        return (repo, await JobWorktree.AddAsync(repo, "job-1", Ct));
    }

    private static string Commit(string directory, string file, string content)
    {
        File.WriteAllText(Path.Combine(directory, file), content);
        GitRepo.Run(directory, "add", "-A");
        GitRepo.Run(directory, "commit", "-q", "-m", $"change {file}");
        return GitRepo.Head(directory);
    }

    private static async Task<string> RefusalAsync(string repo)
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.RemoveAsync(repo, "job-1", Ct));
        return ex.Message;
    }

    [Fact]
    public async Task ACommitResetAwayIsRefusedNamingTheCountAndTheShaAndFreedOnceABranchKeepsItAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string lost = Commit(worktree.Path, "lost.txt", "only in the reflog\n");
        GitRepo.Run(worktree.Path, "reset", "-q", "--hard", "HEAD~1");

        string message = await RefusalAsync(repo);

        Assert.Contains("its HEAD reflog holds 1 commit that no branch, tag or other ref contains", message, StringComparison.Ordinal);
        Assert.Contains(lost, message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));

        GitRepo.Run(repo, "branch", "saved", lost);

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal(lost, GitRepo.RevParse(repo, "saved"));
    }

    [Fact]
    public async Task AnAmendThatChangedTheContentIsRefusedBecauseTheOldCommitIsOnlyInTheReflogAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string first = Commit(worktree.Path, "work.txt", "first version\n");
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "amended version\n");
        GitRepo.Run(worktree.Path, "add", "-A");
        GitRepo.Run(worktree.Path, "commit", "-q", "--amend", "--no-edit");

        string message = await RefusalAsync(repo);

        Assert.Contains("its HEAD reflog holds", message, StringComparison.Ordinal);
        Assert.Contains(first, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanRebaseOntoAnAdvancedBranchIsFreedBecauseTheBranchCarriesTheSamePatchAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "work.txt", "the job's work\n");
        Commit(repo, "other.txt", "main moved on\n");
        GitRepo.Run(worktree.Path, "rebase", "-q", "main");

        string? warning = await JobWorktree.RemoveAsync(repo, "job-1", Ct);

        Assert.Null(warning);
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal("the job's work\n", GitRepo.Run(repo, "show", "claustrum/job-1:work.txt") + "\n");
    }

    [Fact]
    public async Task ARebaseWithAResolvedConflictIsRefusedBecauseThePatchChangedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "seed.txt", "the job's line\n");
        Commit(repo, "seed.txt", "main's line\n");
        (int exit, _, _) = GitRepo.Try(worktree.Path, "rebase", "main");
        Assert.NotEqual(0, exit);
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "a resolution that is neither\n");
        GitRepo.Run(worktree.Path, "add", "seed.txt");
        GitRepo.Try(worktree.Path, "-c", "core.editor=true", "rebase", "--continue");

        string message = await RefusalAsync(repo);

        Assert.Contains("its HEAD reflog holds", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task AfterTheReflogIsExpiredOfUnreachableCommitsTheFreeGoesThroughAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "lost.txt", "dropped on purpose\n");
        GitRepo.Run(worktree.Path, "reset", "-q", "--hard", "HEAD~1");
        await RefusalAsync(repo);

        GitRepo.Run(worktree.Path, "reflog", "expire", "--expire-unreachable=now", "HEAD");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
        Assert.False(Directory.Exists(worktree.Path));
    }

    // More than the 100 shas one rev-list call is given: every reflog entry is reachable here, so nothing is lost either way.
    [Fact]
    public async Task MoreThanAHundredReflogEntriesAreCheckedInChunksAndAllReachableOnesFreeAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the commits are made by an sh loop.");

        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        (int exit, _, string error) = RunSh(worktree.Path, "i=0; while [ $i -lt 130 ]; do git commit -q --allow-empty -m \"c$i\" || exit 1; i=$((i+1)); done");
        Assert.True(exit == 0, error);
        Assert.True(GitRepo.Run(worktree.Path, "reflog", "show", "--format=%H", "HEAD").Split('\n').Length > 100);

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
        Assert.False(Directory.Exists(worktree.Path));
    }

    [Fact]
    public async Task ADanglingMergeIsRefusedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "-b", "side");
        Commit(worktree.Path, "side.txt", "side\n");
        GitRepo.Run(worktree.Path, "checkout", "-q", "claustrum/job-1");
        Commit(worktree.Path, "mine.txt", "mine\n");
        GitRepo.Run(worktree.Path, "merge", "-q", "--no-ff", "-m", "merge side", "side");
        GitRepo.Run(worktree.Path, "reset", "-q", "--hard", "HEAD~1");
        GitRepo.Run(repo, "branch", "-q", "-D", "side");

        string message = await RefusalAsync(repo);

        Assert.Contains("its HEAD reflog holds", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSh(string directory, string script)
    {
        System.Diagnostics.ProcessStartInfo start = new("sh") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("sh did not start");
        string error = process.StandardError.ReadToEnd();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
