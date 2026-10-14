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

    // Round 3: the BRANCH's reflog lives in the shared common directory and survives the free, so what was committed while
    // on the branch is not lost by a reset or an amend. A commit made on a detached HEAD, then left, is.
    [Fact]
    public async Task ACommitResetAwayWhileOnTheBranchIsFreedBecauseTheBranchReflogKeepsItAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string kept = Commit(worktree.Path, "kept.txt", "in the branch's reflog\n");
        GitRepo.Run(worktree.Path, "reset", "-q", "--hard", "HEAD~1");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));

        Assert.False(Directory.Exists(worktree.Path));
        Assert.Contains(kept, GitRepo.Run(repo, "reflog", "show", "--format=%H", "claustrum/job-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResetOfTwoCommitsIsFreedTooAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "one.txt", "1\n");
        Commit(worktree.Path, "two.txt", "2\n");
        GitRepo.Run(worktree.Path, "reset", "-q", "--hard", "HEAD~2");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
    }

    private static string DetachedCommitThenBackToTheBranch(string worktree)
    {
        GitRepo.Run(worktree, "checkout", "-q", "--detach");
        string lost = Commit(worktree, "lost.txt", "made while detached\n");
        GitRepo.Run(worktree, "checkout", "-q", "claustrum/job-1");
        return lost;
    }

    [Fact]
    public async Task ADetachedHeadCommitThenBackOnTheBranchIsRefusedNamingTheCountAndTheShaAndFreedOnceABranchKeepsItAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string lost = DetachedCommitThenBackToTheBranch(worktree.Path);

        string message = await RefusalAsync(repo);

        Assert.Contains("hold 1 commit that no branch, tag or other ref contains", message, StringComparison.Ordinal);
        Assert.Contains(lost, message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));

        GitRepo.Run(repo, "branch", "saved", lost);

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Equal(lost, GitRepo.RevParse(repo, "saved"));
    }

    [Fact]
    public async Task AnAmendThatChangedTheContentWhileOnTheBranchIsFreedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "work.txt", "first version\n");
        File.WriteAllText(Path.Combine(worktree.Path, "work.txt"), "amended version\n");
        GitRepo.Run(worktree.Path, "add", "-A");
        GitRepo.Run(worktree.Path, "commit", "-q", "--amend", "--no-edit");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
    }

    // Per-worktree refs live in the admin directory too: a commit only they reach is lost with it.
    [Theory]
    [InlineData("refs/worktree/x")]
    [InlineData("refs/bisect/bad")]
    public async Task ACommitReachableOnlyFromAPerWorktreeRefIsRefusedAsync(string reference)
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");
        string lost = Commit(worktree.Path, "lost.txt", "only a worktree ref holds it\n");
        GitRepo.Run(worktree.Path, "checkout", "-q", "claustrum/job-1");
        GitRepo.Run(worktree.Path, "reflog", "expire", "--expire-unreachable=now", "HEAD");
        GitRepo.Run(worktree.Path, "update-ref", reference, lost);

        string message = await RefusalAsync(repo);

        Assert.Contains("refs of its own hold", message, StringComparison.Ordinal);
        Assert.Contains(lost, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorktreeRefToABlobOrToACommitTheBranchHoldsIsFreedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string blob = GitRepo.Run(worktree.Path, "rev-parse", "HEAD:seed.txt");
        GitRepo.Run(worktree.Path, "update-ref", "refs/worktree/blob", blob);
        GitRepo.Run(worktree.Path, "update-ref", "refs/worktree/head", GitRepo.Head(worktree.Path));

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
    }

    [Fact]
    public async Task AHeadPointingOutsideRefsHeadsIsRefusedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "update-ref", "refs/other/place", GitRepo.Head(worktree.Path));
        GitRepo.Run(worktree.Path, "symbolic-ref", "HEAD", "refs/other/place");

        string message = await RefusalAsync(repo);

        Assert.Contains("refs/other/place", message, StringComparison.Ordinal);
        Assert.Contains("not a branch", message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(worktree.Path));
    }

    // `log.showSignature` puts gpg output in `git log -g`; the check reads `rev-list --walk-reflogs`, so it is not confused by it.
    [Fact]
    public async Task LogShowSignatureWithAFakeGpgProgramDoesNotConfuseTheCheckAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the fake gpg program is an sh script.");

        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        string gpg = Path.Combine(scratch.CreateDirectory(), "fake-gpg");
        // Signs (a fake signature) and verifies (a good one, with gpg's usual line on stderr), so `log -g` prints gpg lines.
        File.WriteAllText(gpg, """
            #!/bin/sh
            case "$*" in
              *--verify*)
                echo '[GNUPG:] NEWSIG'
                echo '[GNUPG:] GOODSIG 0123456789ABCDEF nobody'
                echo '[GNUPG:] VALIDSIG 0123456789ABCDEF 2026-10-14 1 0 4 0 1 8 00 0123456789ABCDEF'
                echo '[GNUPG:] TRUST_ULTIMATE'
                echo 'gpg: Good signature from "nobody"' >&2
                exit 0 ;;
              *)
                cat >/dev/null
                echo '[GNUPG:] SIG_CREATED D 1 8 00 1 0123456789ABCDEF' >&2
                printf -- '-----BEGIN PGP SIGNATURE-----\n\nZmFrZQ==\n-----END PGP SIGNATURE-----\n'
                exit 0 ;;
            esac
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(gpg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        GitRepo.Run(repo, "config", "log.showSignature", "true");
        GitRepo.Run(repo, "config", "gpg.program", gpg);
        GitRepo.Run(repo, "config", "user.signingkey", "FAKEKEY");
        GitRepo.Run(repo, "config", "commit.gpgsign", "true");
        Commit(worktree.Path, "work.txt", "work\n");
        Assert.Contains("Good signature", GitRepo.Try(worktree.Path, "log", "-g", "--format=%H", "HEAD").Stdout + GitRepo.Try(worktree.Path, "log", "-g", "--format=%H", "HEAD").Stderr, StringComparison.Ordinal);

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
        Assert.False(Directory.Exists(worktree.Path));
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

    // The rebase's final commit is on the branch, and the old tip stays in the branch's reflog: nothing is lost.
    [Fact]
    public async Task ARebaseWithAResolvedConflictIsFreedBecauseTheBranchReflogKeepsTheOldTipAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Commit(worktree.Path, "seed.txt", "the job's line\n");
        Commit(repo, "seed.txt", "main's line\n");
        (int exit, _, _) = GitRepo.Try(worktree.Path, "rebase", "main");
        Assert.NotEqual(0, exit);
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "a resolution that is neither\n");
        GitRepo.Run(worktree.Path, "add", "seed.txt");
        GitRepo.Try(worktree.Path, "-c", "core.editor=true", "rebase", "--continue");

        Assert.Null(await JobWorktree.RemoveAsync(repo, "job-1", Ct));
    }

    [Fact]
    public async Task AfterTheReflogIsExpiredOfUnreachableCommitsTheFreeGoesThroughAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        DetachedCommitThenBackToTheBranch(worktree.Path);
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
    public async Task ADanglingMergeMadeWhileDetachedIsRefusedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "checkout", "-q", "-b", "side");
        Commit(worktree.Path, "side.txt", "side\n");
        GitRepo.Run(worktree.Path, "checkout", "-q", "claustrum/job-1");
        Commit(worktree.Path, "mine.txt", "mine\n");
        GitRepo.Run(worktree.Path, "checkout", "-q", "--detach");
        GitRepo.Run(worktree.Path, "merge", "-q", "--no-ff", "-m", "merge side", "side");
        GitRepo.Run(worktree.Path, "checkout", "-q", "claustrum/job-1");
        GitRepo.Run(repo, "branch", "-q", "-D", "side");

        string message = await RefusalAsync(repo);

        Assert.Contains("hold", message, StringComparison.Ordinal);
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
