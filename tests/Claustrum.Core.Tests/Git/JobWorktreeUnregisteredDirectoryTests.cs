using System.Diagnostics;
using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;
// Claustrum.Core.Tests.Process is an enclosing-namespace member and wins over a using (see GitRepo): renamed.
using SystemProcess = System.Diagnostics.Process;

namespace Claustrum.Core.Tests.Git;

// #108: a `git worktree add` that is killed or fails before git registers the worktree leaves a directory `worktree remove`
// calls "not a working tree". The undo deletes it, but only when this add made it, it is a job directory of this repository,
// no link itself, and git lists no worktree there. A script named `git` first on PATH plays the half-done add (it only makes
// the directory, or sleeps in it) and the listing, and hands everything else to the real git, so the collection runs alone.
// Unix only: the fake is sh. Paths git prints are realpaths (macOS: /var is /private/var), so the listing cases print both spellings.
[Collection(FakeGitCollectionDefinition.Name)]
public sealed class JobWorktreeUnregisteredDirectoryTests : IDisposable
{
    private const string JobId = "job-1";
    private const string Existing = "feature/keep";

    private readonly ScratchRoot scratch = new("claustrum-unregistered-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // `worktree add -q <path> …`: the path is $4.
    private static FakeGit FakeAdd(string body, string extra = "") => FakeGit.Scripted($$"""
        case "$*" in
          *'worktree add'*) {{body}};;
          {{extra}}
        esac
        """);

    private string SeededRepo(bool existingBranch)
    {
        string repo = scratch.CreateSeededRepo();
        if (existingBranch)
            GitRepo.Run(repo, "branch", Existing);

        return repo;
    }

    private async Task<Exception> FailedAddAsync(string repo, bool existingBranch, string jobId = JobId) =>
        existingBranch
            ? await Assert.ThrowsAsync<BranchRefusedException>(() => JobWorktree.CheckOutBranchAsync(repo, jobId, Existing, scratch.CreateDirectory(), Ct))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(repo, jobId, Ct));

    private static void AssertNothingLeft(string repo, bool existingBranch, string? tip)
    {
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, JobId)));
        Assert.Single(GitRepo.WorktreePaths(repo));
        if (existingBranch)
        {
            Assert.Contains(Existing, GitRepo.Branches(repo));
            Assert.Equal(tip, GitRepo.RevParse(repo, $"refs/heads/{Existing}"));
        }
        else
        {
            Assert.DoesNotContain(JobWorktree.BranchFor(JobId), GitRepo.Branches(repo));
        }
    }

    // The shell's own realpath: what git prints for a worktree (`pwd -P` is POSIX, `realpath` is not on every macOS).
    private static string RealPath(string directory)
    {
        ProcessStartInfo start = new("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cd \"$1\" && pwd -P");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(directory);
        using SystemProcess process = SystemProcess.Start(start) ?? throw new InvalidOperationException("sh did not start");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    private string OutsideDirectory()
    {
        string outside = scratch.CreateDirectory();
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine\n");
        return outside;
    }

    // ---- the directory is deleted --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADirectoryTheFailedAddMadeAndGitNeverRegisteredIsDeletedWithNoLeftBehindAsync(bool existingBranch)
    {
        string repo = SeededRepo(existingBranch);
        string tip = existingBranch ? GitRepo.RevParse(repo, $"refs/heads/{Existing}") : "";
        Exception ex;
        using (FakeAdd("""mkdir -p "$4/sub"; echo partial > "$4/sub/half.txt"; exit 1"""))
            ex = await FailedAddAsync(repo, existingBranch);

        Assert.DoesNotContain("left behind", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft(repo, existingBranch, tip);
    }

    // The kill lands after git made the directory and before it wrote the registration: the cancel path of the same undo.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelOfAnAddThatOnlyMadeTheDirectoryLeavesNoDirectoryAndStaysACancelAsync(bool existingBranch)
    {
        string repo = SeededRepo(existingBranch);
        string tip = existingBranch ? GitRepo.RevParse(repo, $"refs/heads/{Existing}") : "";
        string partial = Path.Combine(JobWorktree.PathFor(repo, JobId), "partial.txt");
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using (FakeAdd("""mkdir -p "$4"; echo half > "$4/partial.txt"; exec sleep 29"""))
        {
            Task trigger = Task.Run(async () =>
            {
                while (!File.Exists(partial))
                    await Task.Delay(20, Ct);

                await cancel.CancelAsync();
            }, Ct);

            OperationCanceledException ex = existingBranch
                ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.CheckOutBranchAsync(repo, JobId, Existing, scratch.CreateDirectory(), cancel.Token))
                : await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.AddAsync(repo, JobId, cancel.Token));
            await trigger;

            Assert.DoesNotContain("left behind", ex.Message, StringComparison.Ordinal);
        }

        AssertNothingLeft(repo, existingBranch, tip);
    }

    // The add is cancelled while git holds the registration locked `initializing`: `-f -f` clears it, no directory is left.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelOnceTheEntryIsRegisteredAndLockedInitializingStillRemovesItAsync(bool existingBranch)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("the slow smudge filter is `sleep 10; cat` run by git's sh; its availability on a Windows runner is not verified.");

        string repo = scratch.CreateRepo();
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".claustrum/\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.txt filter=slow\n");
        GitRepo.Run(repo, "config", "filter.slow.clean", "cat");
        GitRepo.Run(repo, "config", "filter.slow.smudge", "sleep 10; cat");
        GitRepo.Commit(repo, "seed.txt", "seed\n");
        if (existingBranch)
            GitRepo.Run(repo, "branch", Existing);

        string tip = existingBranch ? GitRepo.RevParse(repo, $"refs/heads/{Existing}") : "";
        string locked = Path.Combine(repo, ".git", "worktrees", JobId, "locked");
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task trigger = Task.Run(async () =>
        {
            while (!File.Exists(locked))
                await Task.Delay(20, Ct);

            await cancel.CancelAsync();
        }, Ct);

        OperationCanceledException ex = existingBranch
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.CheckOutBranchAsync(repo, JobId, Existing, scratch.CreateDirectory(), cancel.Token))
            : await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JobWorktree.AddAsync(repo, JobId, cancel.Token));
        await trigger;

        Assert.DoesNotContain("left behind", ex.Message, StringComparison.Ordinal);
        AssertNothingLeft(repo, existingBranch, tip);
    }

    // The delete does not follow a link inside the partial directory: whatever it points at outside is as it was.
    [Fact]
    public async Task ALinkInsideThePartialDirectoryIsRemovedAndNeverFollowedOutAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        string outside = OutsideDirectory();
        string keep = Path.Combine(outside, "keep.txt");
        string body = $$"""mkdir -p "$4"; ln -s '{{outside}}' "$4/escape-dir"; ln -s '{{keep}}' "$4/escape-file"; exit 1""";
        Exception ex;
        using (FakeAdd(body))
            ex = await FailedAddAsync(repo, existingBranch: false);

        Assert.DoesNotContain("left behind", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(JobWorktree.PathFor(repo, JobId)));
        Assert.Equal("mine\n", File.ReadAllText(keep));
        Assert.Equal([keep], Directory.GetFiles(outside));
    }

    // ---- the directory stays ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADirectoryThatExistedBeforeTheAddIsNotDeletedAndTheTextSaysWhyAsync(bool existingBranch)
    {
        string repo = SeededRepo(existingBranch);
        string job = JobWorktree.PathFor(repo, JobId);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "mine.txt"), "mine\n");
        Exception ex;
        using (FakeGit.Scripted("""case "$*" in *'worktree add'*) exit 1;; esac"""))
            ex = await FailedAddAsync(repo, existingBranch);

        Assert.Contains("left behind", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not deleted: it existed before the add", ex.Message, StringComparison.Ordinal);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(job, "mine.txt")));
    }

    // An empty directory is the case git itself accepts for an add; the fake refuses it, so the undo meets it unregistered.
    [Fact]
    public async Task AnEmptyDirectoryThatExistedBeforeTheAddIsNotDeletedAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        string job = Directory.CreateDirectory(JobWorktree.PathFor(repo, JobId)).FullName;
        Exception ex;
        using (FakeGit.Scripted("""case "$*" in *'worktree add'*) exit 1;; esac"""))
            ex = await FailedAddAsync(repo, existingBranch: false);

        Assert.Contains("not deleted: it existed before the add", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(job));
    }

    [Fact]
    public async Task AJobPathThatWasALinkBeforeTheAddIsLeftInPlaceAndItsTargetUntouchedAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        string outside = OutsideDirectory();
        string job = JobWorktree.PathFor(repo, JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(job) ?? repo);
        Directory.CreateSymbolicLink(job, outside);
        Exception ex;
        using (FakeGit.Scripted("""case "$*" in *'worktree add'*) exit 1;; esac"""))
            ex = await FailedAddAsync(repo, existingBranch: false);

        Assert.Contains("not deleted: it existed before the add", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(new DirectoryInfo(job).LinkTarget);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    // The add itself swapped a link in at the job path (the guard before the add saw nothing there): the link is not followed.
    [Fact]
    public async Task AJobPathThatBecameALinkDuringTheAddIsLeftInPlaceAndItsTargetUntouchedAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        string outside = OutsideDirectory();
        string job = JobWorktree.PathFor(repo, JobId);
        Exception ex;
        using (FakeAdd($"ln -s '{outside}' \"$4\"; exit 1"))
            ex = await FailedAddAsync(repo, existingBranch: false);

        Assert.Contains("not deleted: it is a link", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(new DirectoryInfo(job).LinkTarget);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    // A job id with a separator puts the directory outside `.claustrum/worktrees`: not a job directory of this repository.
    [Theory]
    [InlineData("../not-a-job")]
    [InlineData("sub/job-1")]
    public async Task ADirectoryThatIsNotDirectlyUnderTheJobsDirectoryIsNotDeletedAsync(string jobId)
    {
        string repo = SeededRepo(existingBranch: false);
        string path = JobWorktree.PathFor(repo, jobId);
        Exception ex;
        using (FakeAdd("""mkdir -p "$4"; exit 1"""))
            ex = await FailedAddAsync(repo, existingBranch: false, jobId);

        Assert.Contains("not deleted: it is not a job directory of this repository's .claustrum/worktrees", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    // git's answer is compared as typed and as git spells it: a repository reached through a link is listed under its realpath.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADirectoryGitListsAsAWorktreeIsKeptInEitherSpellingAsync(bool listedAsRealPath)
    {
        string repo = SeededRepo(existingBranch: false);
        string link = Path.Combine(scratch.CreateDirectory(), "repo-link");
        Directory.CreateSymbolicLink(link, repo);
        string typed = JobWorktree.PathFor(link, JobId);
        string real = Path.Combine(RealPath(repo), ".claustrum", "worktrees", JobId);
        string listed = listedAsRealPath ? real : typed;

        Exception ex;
        using (FakeAdd("""mkdir -p "$4"; exit 1""", $$"""*'worktree list'*) printf 'worktree %s\n\n' '{{listed}}'; exit 0;;"""))
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => JobWorktree.AddAsync(link, JobId, Ct));

        Assert.Contains("not deleted: git lists it as a worktree", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(real));
    }

    [Fact]
    public async Task ADirectoryIsKeptWhenGitCannotListTheWorktreesAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        Exception ex;
        using (FakeAdd("""mkdir -p "$4"; exit 1""", "*'worktree list'*) exit 1;;"))
            ex = await FailedAddAsync(repo, existingBranch: false);

        Assert.Contains("not deleted: git could not list the worktrees", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(JobWorktree.PathFor(repo, JobId)));
    }

    // A read-only subdirectory cannot be emptied: the delete fails, the directory is reported with the reason, and the branch still goes.
    [Fact]
    public async Task ADirectoryThatCannotBeDeletedIsReportedWithTheReasonAsync()
    {
        string repo = SeededRepo(existingBranch: false);
        string sub = Path.Combine(JobWorktree.PathFor(repo, JobId), "sub");
        Exception ex;
        try
        {
            using (FakeAdd("""mkdir -p "$4/sub"; echo x > "$4/sub/f"; chmod 555 "$4/sub"; exit 1"""))
                ex = await FailedAddAsync(repo, existingBranch: false);

            if (!Directory.Exists(sub))
                Assert.Skip("this account is not stopped by a read-only directory (root, or a capability that bypasses modes).");

            Assert.Contains($"left behind: {JobWorktree.PathFor(repo, JobId)} (", ex.Message, StringComparison.Ordinal);
            Assert.Matches(@"; not deleted: \S", ex.Message);
            Assert.DoesNotContain("not deleted: it ", ex.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(sub, "f")));
            Assert.DoesNotContain(JobWorktree.BranchFor(JobId), GitRepo.Branches(repo));
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(sub))
                File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
