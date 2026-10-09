using Claustrum.Core.Git;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #74 rounds 3–5: the runner's commit decides nothing from `git add`'s exit code or words — they are translated, and exit
// 1 also covers a path skipped without a word (sparse-checkout). What it commits is whatever the INDEX holds after the
// add; what it warns about is whatever the STATUS after the add still shows unstaged, by shape: a submodule, a directory
// with a `.git` of its own, anything else (NOTES.md "Review round 4"). Every case is a real worktree of a real
// repository; assertions read git back, never the code's own idea of what it did. English git output is asserted only
// in the end-to-end tests that pin LC_ALL; here only the parts git never translates (`error:`/`fatal:` prefixes, paths).
public sealed class JobWorktreeLeftOutTests : IDisposable
{
    private const int Cap = 1_000_000;
    private const string Branch = "claustrum/job-1";

    private readonly ScratchRoot scratch = new("claustrum-worktree-");

    public void Dispose()
    {
        foreach (string path in unreadable.Where(File.Exists))
            SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        scratch.Dispose();
    }

    private readonly List<string> unreadable = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Repo, JobWorktreeInfo Worktree)> StartAsync(Action<string>? prepare = null)
    {
        string repo = scratch.CreateSeededRepo();
        prepare?.Invoke(repo);
        return (repo, await JobWorktree.AddAsync(repo, "job-1", Ct));
    }

    private static Task<BranchReceipt> CommitAsync(JobWorktreeInfo worktree, string? log = null) =>
        JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, log, Ct);

    // `--ignore-submodules=none`: `git show` would honour a repository's `diff.ignoreSubmodules=all` and list no moved pointer.
    private static string[] CommittedPaths(string repo, string? commit) =>
        commit is null
            ? []
            : [.. GitRepo.Run(repo, "diff-tree", "-r", "--no-commit-id", "--name-only", "--ignore-submodules=none", commit).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static string[] Staged(JobWorktreeInfo worktree) =>
        [.. GitRepo.Run(worktree.Path, "diff", "--cached", "--name-only", "--ignore-submodules=none").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static void Write(JobWorktreeInfo worktree, string relative, string content = "work\n")
    {
        string path = Path.Combine(worktree.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
        File.WriteAllText(path, content);
    }

    private static string LeftOutPointer(string path) =>
        $"{path} left out of the commit on {Branch}: git did not stage it (sparse-checkout, permissions or a filter — see the job's stderr.log)";

    private static string EmbeddedRepository(string path) =>
        $"embedded repository at {path} left out of the commit on {Branch} — move it out or add it as a submodule";

    private static string SubmoduleChanges(string path) =>
        $"changes inside submodule {path} left out of the commit on {Branch} — commit them in the submodule, then stage its pointer";

    private static void RequireReadableFileModes()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            Assert.Skip("an unreadable file needs POSIX permissions and a user that does not bypass them (not root).");
    }

    private void MakeUnreadable(JobWorktreeInfo worktree, string relative)
    {
        string path = Path.Combine(worktree.Path, relative);
        unreadable.Add(path);
        SetMode(path, UnixFileMode.None);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }

    private static void RequireGlobCharactersInFileNames(string name)
    {
        if (OperatingSystem.IsWindows() && name.Contains('*', StringComparison.Ordinal))
            Assert.Skip("Windows does not allow `*` in a file name.");
    }

    private string SubmoduleSource() => scratch.CreateSeededRepo();

    private static void AddSubmodule(string where, string source, string path) =>
        GitRepo.Run(where, "-c", "protocol.file.allow=always", "submodule", "add", "-q", source, path);

    // ---- sparse-checkout: exit 1 with no `error:` line at all ----------------------------------------------------

    private static void MakeSparseOnA(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, "a"));
        Directory.CreateDirectory(Path.Combine(repo, "b"));
        File.WriteAllText(Path.Combine(repo, "a", "x.txt"), "a\n");
        File.WriteAllText(Path.Combine(repo, "b", "x.txt"), "b\n");
        GitRepo.Run(repo, "add", "-A");
        GitRepo.Run(repo, "commit", "-q", "-m", "a and b");
        GitRepo.Run(repo, "sparse-checkout", "set", "--cone", "a");
    }

    [Fact]
    public async Task APathOutsideTheSparseConeIsLeftOutWithAWarningAndTheRestIsCommittedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(MakeSparseOnA);
        Assert.False(Directory.Exists(Path.Combine(worktree.Path, "b")), "the premise: the new worktree is sparse like the repository");
        Write(worktree, "a/new.txt");
        Write(worktree, "b/new.txt");
        string log = Path.Combine(scratch.CreateDirectory(), "stderr.log");

        BranchReceipt receipt = await CommitAsync(worktree, log);

        Assert.Equal(["a/new.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([LeftOutPointer("b/new.txt")], receipt.Warnings);
        string written = File.ReadAllText(log);
        Assert.StartsWith($"claustrum: git add -A in {worktree.Path}, for the commit on {Branch}:", written, StringComparison.Ordinal);
        Assert.Contains("b/new.txt", written, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "b", "new.txt")), "what was left out is left where the role wrote it");
        Assert.Equal("?? b/new.txt", GitRepo.Status(worktree.Path));
    }

    // A caller with no job directory (the 5-argument overload) gets git's own line on the warning instead of a pointer.
    [Fact]
    public async Task WithNoLogToWriteGitsOwnLineRidesOnTheWarningInsteadAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync(MakeSparseOnA);
        Write(worktree, "a/new.txt");
        Write(worktree, "b/new.txt");

        BranchReceipt receipt = await JobWorktree.CommitRunAsync(worktree, "claustrum builder job-1", new SnapshotDiff([], null, false), Cap, Ct);

        string warning = Assert.Single(receipt.Warnings);
        Assert.StartsWith($"b/new.txt left out of the commit on {Branch}: git did not stage it (sparse-checkout, permissions or a filter — git said: ", warning, StringComparison.Ordinal);
        Assert.EndsWith(")", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr.log", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnwritableLogFallsBackToGitsOwnLineOnTheWarningAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync(MakeSparseOnA);
        Write(worktree, "a/new.txt");
        Write(worktree, "b/new.txt");
        string log = Path.Combine(scratch.CreateDirectory(), "no-such-directory", "stderr.log");

        BranchReceipt receipt = await CommitAsync(worktree, log);

        Assert.Contains("git did not stage it (sparse-checkout, permissions or a filter — git said: ", Assert.Single(receipt.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAPathOutsideTheConeMakesNoCommitAndSaysWhyItStagedNothingAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(MakeSparseOnA);
        Write(worktree, "b/new.txt");
        string log = Path.Combine(scratch.CreateDirectory(), "stderr.log");

        BranchReceipt receipt = await CommitAsync(worktree, log);

        Assert.Null(receipt.Commit);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal(2, receipt.Warnings.Length);
        Assert.Equal(LeftOutPointer("b/new.txt"), receipt.Warnings[0]);
        Assert.StartsWith($"work left uncommitted on {Branch}: git add staged nothing: ", receipt.Warnings[1], StringComparison.Ordinal);
        Assert.Empty(Staged(worktree));
    }

    // ---- a directory with a `.git` of its own -----------------------------------------------------------------------

    [Fact]
    public async Task ACommitLessEmbeddedRepositoryIsLeftOutAndTheRealFileIsCommittedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "init", "-q", "vendor/x");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([EmbeddedRepository("vendor/x")], receipt.Warnings);
        Assert.Equal("?? vendor/x/", GitRepo.Status(worktree.Path));
        Assert.Empty(Staged(worktree));
    }

    [Fact]
    public async Task ACommitLessEmbeddedRepositoryAloneMakesNoCommitAndOnlyTheEmbeddedWarningAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "init", "-q", "vendor/x");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Equal([EmbeddedRepository("vendor/x")], receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task AStrayCloneAndACommitLessRepositoryCostOneWarningEachAndTheRealFileIsStillCommittedAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "init", "-q", "vendor/x");
        GitRepo.Run(worktree.Path, "clone", "-q", repo, "vendor/y");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([EmbeddedRepository("vendor/x"), EmbeddedRepository("vendor/y")], receipt.Warnings);
        Assert.Equal(["?? vendor/x/", "?? vendor/y/"], GitRepo.Status(worktree.Path).Split('\n'));
        Assert.Empty(Staged(worktree));
    }

    // G3: the clone staged as a new gitlink is unstaged on its own — the rest of the work is not given up with it.
    [Fact]
    public async Task AStrayCloneAloneIsUnstagedMakesNoCommitAndLeavesAnEmptyIndexAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "clone", "-q", repo, "vendor/y");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Equal([EmbeddedRepository("vendor/y")], receipt.Warnings);
        Assert.Empty(Staged(worktree));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // A gitlink the staged `.gitmodules` lists is a submodule the role added on purpose.
    [Fact]
    public async Task ADeliberateSubmoduleAddIsCommittedWithItsGitmodulesWhileAStrayCloneBesideItIsNotAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        AddSubmodule(worktree.Path, SubmoduleSource(), "deps/lib");
        GitRepo.Run(worktree.Path, "clone", "-q", repo, "vendor/y");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal([".gitmodules", "deps/lib", "real.txt"], [.. CommittedPaths(repo, receipt.Commit).Order(StringComparer.Ordinal)]);
        Assert.StartsWith("160000 commit ", GitRepo.Run(repo, "ls-tree", receipt.Commit ?? "", "--", "deps/lib"), StringComparison.Ordinal);
        Assert.Equal([EmbeddedRepository("vendor/y")], receipt.Warnings);
    }

    // Paths come from git as raw bytes (`-z`), never quoted: a name with a space and non-ASCII characters is named as it
    // is, and unstaged by name — a glob character in it must not become a pattern (`reset --literal-pathspecs`).
    [Theory]
    [InlineData("vendor/my ünï lib")]
    [InlineData("vendor/a[b]*")]
    public async Task AStrayCloneWhoseNameHasSpacesNonAsciiOrGlobCharactersIsNamedRawAndUnstagedByNameAsync(string name)
    {
        RequireGlobCharactersInFileNames(name);
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "clone", "-q", repo, name);
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([EmbeddedRepository(name)], receipt.Warnings);
        Assert.Empty(Staged(worktree));
    }

    // The unstage is by NAME: a stray clone called `v*` must not take the real file `vreal.txt` out of the commit with it,
    // as `git reset -- 'v*'` would (the pathspec is a glob unless git is told it is literal).
    [Fact]
    public async Task AStrayClonesNameIsNeverAGlobThatUnstagesTheRealFilesItMatchesAsync()
    {
        RequireGlobCharactersInFileNames("v*");
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        GitRepo.Run(worktree.Path, "clone", "-q", repo, "v*");
        Write(worktree, "vreal.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["vreal.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([EmbeddedRepository("v*")], receipt.Warnings);
    }

    // `.gitmodules` is read with `-z`: a submodule's name and path may hold spaces.
    [Fact]
    public async Task ADeliberateSubmoduleInAPathWithASpaceIsCommittedNotTakenForAStrayAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        AddSubmodule(worktree.Path, SubmoduleSource(), "my sub/lib");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal([".gitmodules", "my sub/lib", "real.txt"], [.. CommittedPaths(repo, receipt.Commit).Order(StringComparer.Ordinal)]);
        Assert.Empty(receipt.Warnings);
    }

    // G2: `diff.ignoreSubmodules=all` hid the gitlink from the very diff that finds strays (measured).
    [Fact]
    public async Task AStrayCloneIsStillLeftOutWhenDiffIgnoreSubmodulesIsAllAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(path => GitRepo.Run(path, "config", "diff.ignoreSubmodules", "all"));
        GitRepo.Run(worktree.Path, "clone", "-q", repo, "vendor/y");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([EmbeddedRepository("vendor/y")], receipt.Warnings);
    }

    // ---- an initialised submodule: a dirty file inside it stages nothing -------------------------------------------

    private async Task<(string Repo, JobWorktreeInfo Worktree)> StartWithAnInitialisedSubmoduleAsync(Action<string>? prepare = null)
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(path =>
        {
            AddSubmodule(path, SubmoduleSource(), "sub");
            GitRepo.Run(path, "commit", "-q", "-m", "add sub");
            prepare?.Invoke(path);
        });
        GitRepo.Run(worktree.Path, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "-q", "sub");
        return (repo, worktree);
    }

    [Fact]
    public async Task ADirtyInitialisedSubmoduleAloneMakesNoCommitAndWarnsOfTheChangesInsideItAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartWithAnInitialisedSubmoduleAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "sub", "seed.txt"), "dirty inside the submodule\n");
        Assert.Equal(" M sub", GitRepo.Status(worktree.Path));

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Equal([SubmoduleChanges("sub")], receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
        Assert.Equal(" M sub", GitRepo.Status(worktree.Path));
    }

    [Fact]
    public async Task ADirtySubmoduleBesideARealFileCommitsTheFileAndStillWarnsAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartWithAnInitialisedSubmoduleAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "sub", "seed.txt"), "dirty inside the submodule\n");
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([SubmoduleChanges("sub")], receipt.Warnings);
    }

    // A pointer the role moved by committing inside the submodule IS stageable, and a repository-wide
    // `diff.ignoreSubmodules=all` must not make the commit miss it (G2/H4).
    [Fact]
    public async Task AMovedSubmodulePointerIsCommittedEvenWhenDiffIgnoreSubmodulesIsAllAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartWithAnInitialisedSubmoduleAsync(path => GitRepo.Run(path, "config", "diff.ignoreSubmodules", "all"));
        string sub = Path.Combine(worktree.Path, "sub");
        GitRepo.Run(sub, "config", "user.email", "test@example.com");
        GitRepo.Run(sub, "config", "user.name", "claustrum-tests");
        GitRepo.Run(sub, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(sub, "inside.txt"), "committed inside the submodule\n");
        GitRepo.Run(sub, "add", "-A");
        GitRepo.Run(sub, "commit", "-q", "-m", "inside");
        // The premise: the repository's own setting hides the moved pointer from a plain status.
        Assert.Equal("", GitRepo.Run(worktree.Path, "status", "--porcelain"));

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["sub"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal(GitRepo.Head(sub), GitRepo.Run(repo, "ls-tree", receipt.Commit ?? "", "--", "sub").Split(' ')[2].Split('\t')[0]);
        Assert.Empty(receipt.Warnings);
    }

    // ---- a path git cannot read ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnreadableFileIsLeftOutWithItsOwnWarningAndTheRestIsCommittedAsync()
    {
        RequireReadableFileModes();
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Write(worktree, "real.txt");
        Write(worktree, "secret.txt");
        MakeUnreadable(worktree, "secret.txt");
        string log = Path.Combine(scratch.CreateDirectory(), "stderr.log");

        BranchReceipt receipt = await CommitAsync(worktree, log);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Equal([LeftOutPointer("secret.txt")], receipt.Warnings);
        Assert.Contains("error: open(\"secret.txt\")", File.ReadAllText(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableFileAloneMakesNoCommitAndQuotesGitsErrorLineNotItsAdviceBannerAsync()
    {
        RequireReadableFileModes();
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Write(worktree, "secret.txt");
        MakeUnreadable(worktree, "secret.txt");
        // The ignored machinery directory that makes git print its "paths are ignored" banner FIRST (round 3, H2).
        Write(worktree, ".claustrum/briefs/1-builder.md");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Equal(2, receipt.Warnings.Length);
        Assert.Equal(LeftOutPointer("secret.txt").Replace("see the job's stderr.log", "git said: error", StringComparison.Ordinal)[..40], receipt.Warnings[0][..40]);
        Assert.StartsWith($"work left uncommitted on {Branch}: git add staged nothing: error: open(\"secret.txt\"): ", receipt.Warnings[1], StringComparison.Ordinal);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // The departure from the brief (NOTES.md round 4): "left out" is the status after the add, not "expected − staged".
    // The role staged `seed.txt`, then edited it again and made it unreadable: the commit holds the staged version, the
    // second edit is reported as left out, and the receipt's delta cannot read the file either.
    [Fact]
    public async Task APathTheRoleStagedThenEditedAgainAndMadeUnreadableIsLeftOutNotSilentlyStaleAsync()
    {
        RequireReadableFileModes();
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "version one\n");
        GitRepo.Run(worktree.Path, "add", "seed.txt");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "version two\n");
        MakeUnreadable(worktree, "seed.txt");

        BranchReceipt receipt = await CommitAsync(worktree, Path.Combine(scratch.CreateDirectory(), "stderr.log"));

        Assert.NotNull(receipt.Commit);
        Assert.Equal("version one", GitRepo.Run(repo, "show", $"{receipt.Commit}:seed.txt"));
        Assert.Equal(LeftOutPointer("seed.txt"), receipt.Warnings[0]);
        Assert.StartsWith("receipt delta unavailable, snapshot kept: ", receipt.Warnings[1], StringComparison.Ordinal);
        Assert.Equal(2, receipt.Warnings.Length);
    }

    [Fact]
    public async Task APathStagedThenPutBackAsHeadHasItLeavesNothingToCommitAndNothingToWarnAboutAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "changed\n");
        GitRepo.Run(worktree.Path, "add", "seed.txt");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "seed\n");
        Assert.StartsWith("MM seed.txt", GitRepo.Status(worktree.Path), StringComparison.Ordinal);

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task APathStagedThenDeletedLeavesNothingToCommitAndNothingToWarnAboutAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Write(worktree, "n.txt");
        GitRepo.Run(worktree.Path, "add", "n.txt");
        File.Delete(Path.Combine(worktree.Path, "n.txt"));
        Assert.StartsWith("AD n.txt", GitRepo.Status(worktree.Path), StringComparison.Ordinal);

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Empty(receipt.Warnings);
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    [Fact]
    public async Task ThoseTwoDepartureRowsBesideARealFileCommitOnlyTheRealFileWithNoWarningAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "changed\n");
        GitRepo.Run(worktree.Path, "add", "seed.txt");
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "seed\n");
        Write(worktree, "n.txt");
        GitRepo.Run(worktree.Path, "add", "n.txt");
        File.Delete(Path.Combine(worktree.Path, "n.txt"));
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Empty(receipt.Warnings);
    }

    // A fatal `add` leaves every path unstaged for a reason no path's shape explains; git's own line names the real cause.
    [Fact]
    public async Task AStaleIndexLockLeavesTheFileUncommittedAndNamesTheLockAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Write(worktree, "real.txt");
        string gitDirectory = GitRepo.Run(worktree.Path, "rev-parse", "--absolute-git-dir");
        File.WriteAllText(Path.Combine(gitDirectory, "index.lock"), "");

        BranchReceipt receipt = await CommitAsync(worktree);
        File.Delete(Path.Combine(gitDirectory, "index.lock"));

        Assert.Null(receipt.Commit);
        Assert.Equal(2, receipt.Warnings.Length);
        Assert.StartsWith("real.txt left out of the commit on claustrum/job-1: git did not stage it", receipt.Warnings[0], StringComparison.Ordinal);
        Assert.StartsWith($"work left uncommitted on {Branch}: git add staged nothing: fatal: ", receipt.Warnings[1], StringComparison.Ordinal);
        Assert.Contains("index.lock", receipt.Warnings[1], StringComparison.Ordinal);
        Assert.Equal("?? real.txt", GitRepo.Status(worktree.Path));
        Assert.Equal(worktree.BaseCommit, GitRepo.RevParse(repo, "refs/heads/claustrum/job-1"));
    }

    // ---- Claustrum's own machinery is never the role's work (F1, H2) -----------------------------------------------

    private static async Task<JobWorktreeInfo> AddNestedAsync(JobWorktreeInfo parent) => await JobWorktree.AddAsync(parent.Path, "child-1", Ct);

    // The round-3 HIGH regression: with the machinery directories IGNORED and on disk, `add -A -- . ':(exclude)…'` stages
    // the rest and exits 1 with an advice banner; reading that as a failed add left every architect's leftovers uncommitted.
    [Fact]
    public async Task IgnoredMachineryOnDiskNeverStopsTheLeftoverFromBeingCommittedAndIsNeverCommittedItselfAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync();
        Write(worktree, ".claustrum/briefs/1-builder.md");
        Write(worktree, ".claustrum/locks/default__builder.0.lock", "");
        JobWorktreeInfo child = await AddNestedAsync(worktree);
        Write(worktree, "left.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["left.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Empty(receipt.Warnings);
        Assert.DoesNotContain(".claustrum", GitRepo.Run(repo, "ls-tree", "-r", "--name-only", receipt.Commit ?? ""), StringComparison.Ordinal);
        Assert.True(Directory.Exists(child.Path));
        Assert.True(File.Exists(Path.Combine(worktree.Path, ".claustrum", "briefs", "1-builder.md")));
    }

    // F1: under a whitelist `.gitignore` the nested worktree (`!*/`) and the brief (`!*.md`) are NOT ignored, and the
    // runner still must not commit them — as a `160000` gitlink and a markdown file.
    [Fact]
    public async Task UnignoredMachineryUnderAWhitelistGitignoreIsStillNeverCommittedAsync()
    {
        string repo = scratch.CreateRepo();
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "*\n!*/\n!*.md\n!*.txt\n!.gitignore\n");
        GitRepo.Commit(repo, "seed.txt", "seed\n");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Write(worktree, ".claustrum/briefs/1-builder.md");
        Write(worktree, ".claustrum/locks/default__builder.0.lock", "");
        await AddNestedAsync(worktree);
        Write(worktree, "left.txt");
        Assert.Contains(".claustrum/briefs/1-builder.md", GitRepo.Run(worktree.Path, "status", "--porcelain", "--untracked-files=all"), StringComparison.Ordinal);

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["left.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Empty(receipt.Warnings);
        Assert.DoesNotContain(".claustrum", GitRepo.Run(repo, "ls-tree", "-r", "--name-only", receipt.Commit ?? ""), StringComparison.Ordinal);
        Assert.DoesNotContain("160000", GitRepo.Run(repo, "ls-tree", "-r", receipt.Commit ?? ""), StringComparison.Ordinal);
    }

    // The exclusions are the three directories, not a prefix match on their names.
    [Fact]
    public async Task FilesWhoseNamesOnlyStartLikeTheMachineryOrSitDeeperAreRealWorkAndCommittedAsync()
    {
        string repo = scratch.CreateRepo();
        GitRepo.Commit(repo, "seed.txt", "seed\n");
        JobWorktreeInfo worktree = await JobWorktree.AddAsync(repo, "job-1", Ct);
        Write(worktree, ".claustrum/worktrees-notes.md");
        Write(worktree, ".claustrum/briefs.md");
        Write(worktree, "docs/.claustrum/locks/a");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal([".claustrum/briefs.md", ".claustrum/worktrees-notes.md", "docs/.claustrum/locks/a"], [.. CommittedPaths(repo, receipt.Commit).Order(StringComparer.Ordinal)]);
        Assert.Empty(receipt.Warnings);
    }

    // ---- git settings that changed what the status said ------------------------------------------------------------

    // J3: with `status.showStash=true` and a stash, `status --porcelain=v2 -z` opens with a `# stash 1` field, which
    // the remainder parser took for a path and reported as left out.
    [Fact]
    public async Task AStashUnderShowStashTrueIsNotAPathAndWarnsOfNothingAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(path => GitRepo.Run(path, "config", "status.showStash", "true"));
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "stashed away\n");
        GitRepo.Run(worktree.Path, "stash", "push", "-q");
        Assert.Contains("# stash 1", GitRepo.Run(worktree.Path, "status", "--porcelain=v2"), StringComparison.Ordinal);
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Empty(receipt.Warnings);
        Assert.Single(GitRepo.Run(worktree.Path, "stash", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task AStashAloneUnderShowStashTrueIsNothingToCommitAsync()
    {
        (_, JobWorktreeInfo worktree) = await StartAsync(path => GitRepo.Run(path, "config", "status.showStash", "true"));
        File.WriteAllText(Path.Combine(worktree.Path, "seed.txt"), "stashed away\n");
        GitRepo.Run(worktree.Path, "stash", "push", "-q");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Null(receipt.Commit);
        Assert.Empty(receipt.Warnings);
    }

    [Fact]
    public async Task StatusBranchTrueAddsNoHeaderThatCouldPassForAPathAsync()
    {
        (string repo, JobWorktreeInfo worktree) = await StartAsync(path => GitRepo.Run(path, "config", "status.branch", "true"));
        Write(worktree, "real.txt");

        BranchReceipt receipt = await CommitAsync(worktree);

        Assert.Equal(["real.txt"], CommittedPaths(repo, receipt.Commit));
        Assert.Empty(receipt.Warnings);
    }
}
