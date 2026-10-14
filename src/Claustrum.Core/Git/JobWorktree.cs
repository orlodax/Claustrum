using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Claustrum.Core.IO;
using Claustrum.Core.Jobs;

namespace Claustrum.Core.Git;

// `git worktree add`/`remove` isolation for an isolated job (docs/PLAN.md §D4): one with
// `max_parallel > 1`, or one sent to an existing branch with `--branch` (#63). Each runs inside its own
// `.claustrum/worktrees/<job>` so N concurrent builders never step on the same working tree, and the
// architect integrates by rebasing each branch onto its target rather than reading a shared, contended
// cwd. The runner commits a run's leftovers here too (#61) — NOTES.md "M4 wave 1: a delegate's work
// lands on its branch, and only there".
public static class JobWorktree
{
    // #62: added to an isolated run's deny list, where claude/opencode/copilot enforce it natively. A
    // guard on how the command is written, not a sandbox: `git -C <dir> checkout` is not matched.
    public static readonly IReadOnlyList<string> IsolationDeny = ["git checkout", "git switch"];

    // `git commit` runs the repo's own hooks, `git add` its clean filters (git-lfs, R4) and `worktree add`
    // its checkout — smudge filters, the post-checkout hook (T1): the 30 s git bound would kill a slow one
    // mid-way, leaving work uncommitted or a half-made worktree for a reason nobody chose.
    private static readonly TimeSpan repoCodeTimeout = TimeSpan.FromMinutes(5);

    // #74 F1: Claustrum's own machinery is never a run's work, ignored or not — under a whitelist
    // `.gitignore` (`*`, `!*/`) `add -A` committed a nested job worktree as a `160000` gitlink. Measured
    // 2026-10-09, git 2.56: `:(exclude)` drops those paths from status and add alike, and
    // `--no-literal-pathspecs` keeps a caller's GIT_LITERAL_PATHSPECS=1 from making them literal paths.
    private static readonly string[] allButMachinery =
        ["--", ".", ":(exclude).claustrum/worktrees", ":(exclude).claustrum/briefs", ":(exclude).claustrum/locks"];

    // R2: every status here lists new files whatever `status.showUntrackedFiles` says (repo or global):
    // under `no`, a plain `--porcelain` hid a role's new files from the commit and from the remove check.
    // #74 G2: and every submodule change whatever `diff.ignoreSubmodules` says — status honours `all` (measured).
    private static readonly string[] statusArgs =
        ["--no-literal-pathspecs", "status", "--porcelain", "--untracked-files=all", "--ignore-submodules=none", .. allButMachinery];

    // #74 round 4: what `add` left behind is read back from git, never off its words — v2 for the submodule
    // flag, -z for raw paths (NOTES.md #74 "Review round 4"). Round 5: `status.showStash` adds `# stash N` to v2.
    private static readonly string[] remainderArgs =
        ["--no-literal-pathspecs", "status", "--porcelain=v2", "-z", "--no-show-stash", "--untracked-files=all", "--ignore-submodules=none", .. allButMachinery];

    public static string PathFor(string cwd, string jobId) => Path.Combine(cwd, ".claustrum", "worktrees", jobId);

    public static string BranchFor(string jobId) => $"claustrum/{jobId}";

    // R1: the `.claustrum/locks/` key serialising `--branch` runs on one branch. `@` never survives
    // RoleConcurrencyGate.KeyFor, so no `<cast>__<role>` pool shares it; the hash keeps two branches
    // that sanitise alike (`a/b`, `a_b`) apart and bounds the file name for a long branch.
    public static string LockKeyFor(string branch)
    {
        string readable = new([.. branch.Take(64).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(branch)))[..8];
        return $"branch@{readable}@{hash}";
    }

    public static async Task<JobWorktreeInfo> AddAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        string path = PathFor(cwd, jobId);
        string branch = BranchFor(jobId);

        // F9: the commit is resolved first and handed to git, so BaseCommit is exactly where the branch starts.
        (int headExit, string head, string headError) = await GitProcess.RunAsync(cwd, ["rev-parse", "--verify", "HEAD"], cancellationToken);
        if (headExit != 0)
            throw new InvalidOperationException($"git worktree add {path} -b {branch} failed: no commit to branch from in {cwd} ({Reason("rev-parse", headExit, headError, "")})");

        JobWorktreeInfo worktree = new(path, branch, cwd, head.Trim(), OwnsBranch: true);
        if (await TryAddAsync(cwd, worktree, ["-b", branch, worktree.BaseCommit], cancellationToken) is { } failure)
            throw new InvalidOperationException($"git worktree add {path} -b {branch} failed: {failure}");

        return worktree;
    }

    // `show-ref --verify`, never `rev-parse`: rev-parse also resolves `main~1` under refs/heads/, and
    // `git worktree add <path> main~1` then checks out a detached HEAD the run would commit onto.
    public static async Task<bool> BranchExistsAsync(string cwd, string branch, CancellationToken cancellationToken)
    {
        (int exitCode, _, _) = await FreeGitAsync(cwd, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
        return exitCode == 0;
    }

    public static string NoSuchBranch(string cwd, string branch) =>
        $"--branch {branch}: no local branch of that name in {cwd} (it takes an existing branch, e.g. claustrum/<job id>)";

    /// <summary>
    /// #63: a new job worktree on the existing local <paramref name="branch"/>, not a new branch cut
    /// from HEAD. A branch held by a finished job's worktree under this <paramref name="cwd"/> — one whose
    /// job directory under <paramref name="jobsRoot"/> has its result.json (R3) — is freed first by
    /// removing that worktree, if it is clean; held anywhere else — the main checkout, a running job, a
    /// job this jobs root does not know, a worktree added by hand — it is left alone. Every refusal, git's
    /// own and a git past its bound included, is a <see cref="BranchRefusedException"/> naming the holder,
    /// with no worktree left (F11, T1); a cancel still throws, once the add is undone. The freed worktree's
    /// undeletable trash, if any, rides on <see cref="JobWorktreeInfo.FreeWarning"/>. The caller holds
    /// the branch's lock (<see cref="LockKeyFor"/>, R1) around the whole run.
    /// </summary>
    public static async Task<JobWorktreeInfo> CheckOutBranchAsync(string cwd, string jobId, string branch, string jobsRoot, CancellationToken cancellationToken)
    {
        string tip;
        string? freeWarning;
        try
        {
            (string? refusal, freeWarning) = await FreeBranchAsync(cwd, branch, jobsRoot, cancellationToken);
            if (refusal is not null)
                throw new BranchRefusedException(refusal);

            // The full ref, which show-ref just verified, read before the add: nothing holds the branch now.
            (int tipExit, string tipOutput, _) = await FreeGitAsync(cwd, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
            if (tipExit != 0)
                throw new BranchRefusedException(NoSuchBranch(cwd, branch));

            tip = tipOutput.Trim();
        }
        catch (TimeoutException ex)
        {
            // T1: a git past its bound before the add — a slow free of the old worktree — is a receipt too, not exit 2.
            throw new BranchRefusedException($"--branch {branch}: {ex.Message}");
        }

        // The short name: git checks a local branch out as a branch, where `refs/heads/<name>` here
        // would be a detached HEAD.
        JobWorktreeInfo worktree = new(PathFor(cwd, jobId), branch, cwd, tip, OwnsBranch: false, freeWarning);
        return await TryAddAsync(cwd, worktree, [branch], cancellationToken) is { } failure
            ? throw new BranchRefusedException($"--branch {branch}: {failure}")
            : worktree;
    }

    /// <summary>
    /// `jobs clean`'s removal of a finished job's worktree. Never `--force` (F3): since #61 a finished
    /// worktree is clean, and one that is not — a commit git refused, a hard-killed run, a submodule the
    /// role populated, a new file `status.showUntrackedFiles=no` hides (R2) — is work nobody has looked
    /// at, so it throws naming the path and stays; so does one still holding job worktrees of its own
    /// (#74, a `coordinate` architect's), one on a detached HEAD, mid-operation or holding commits only its own reflog
    /// has, or one that is not this repository's worktree at all. A clean one is renamed aside and unregistered (#106, <see cref="TryFreeAsync"/>).
    /// The branch is untouched either way, so the architect can still rebase from it.
    /// </summary>
    /// <returns>A warning when the freed worktree's trash could not be deleted, else null.</returns>
    public static async Task<string?> RemoveAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        string path = PathFor(cwd, jobId);
        (string? refusal, string? warning) = await TryFreeAsync(cwd, path, cancellationToken);
        return refusal is null ? warning : throw new InvalidOperationException($"{path} left in place: {refusal}");
    }

    /// <summary>
    /// The branch a job's worktree is on, for `jobs clean` to say which branch it keeps — a `--branch` job's is the one it
    /// was given, not <see cref="BranchFor"/>; null for a detached HEAD or a directory git does not read.
    /// </summary>
    public static async Task<string?> HeadBranchAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            (int exitCode, string output, _) = await FreeGitAsync(PathFor(cwd, jobId), ["symbolic-ref", "-q", "--short", "HEAD"], cancellationToken);
            return exitCode == 0 && output.Trim() is { Length: > 0 } branch ? branch : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes a successful <see cref="AddAsync"/>/<see cref="CheckOutBranchAsync"/> for a job that
    /// produced no result (a failed add undoes itself, T1). `--force`, and `branch -D` of a branch the
    /// run created (<c>-b</c>; a `--branch` run's outlives it, #63), only when nothing of the run's can
    /// be in there: on its branch, the branch still at <see cref="JobWorktreeInfo.BaseCommit"/>,
    /// nothing uncommitted. Otherwise — a run whose result.json could not be written after the runner
    /// committed — the branch is kept and the remove is plain, so a dirty worktree stays (F9).
    /// Best-effort: this runs on a failure path whose own exception is the one worth surfacing, so even
    /// a cancel (<see cref="WorktreeInterruptedException"/>) comes back as the return value, never thrown.
    /// </summary>
    /// <returns>Whatever went wrong while cleaning up, or null on success.</returns>
    public static async Task<Exception?> TryRemoveAbandonedAsync(string cwd, JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsUntouchedAsync(worktree, cancellationToken))
            {
                // The trash warning is dropped: this runs on a failure path, and `jobs clean` sweeps what it left.
                return (await TryFreeAsync(cwd, worktree.Path, cancellationToken)).Refusal is { } refusal
                    ? new InvalidOperationException($"{worktree.Path} left in place, branch {worktree.Branch} kept: {refusal}")
                    : null;
            }

            if (await TryForceRemoveAsync(cwd, worktree.Path) is { } failure)
                return new InvalidOperationException($"git worktree remove {worktree.Path} failed: {failure}");

            return await TryDeleteOwnedBranchAsync(cwd, worktree, cancellationToken) is { } branchFailure
                ? new InvalidOperationException($"git branch -D {worktree.Branch} failed: {branchFailure}")
                : null;
        }
        catch (Exception ex) when (IsGitFailure(ex) || ex is OperationCanceledException)
        {
            return ex;
        }
    }

    /// <summary>
    /// #61: commits what an isolated run left in its worktree on its branch, with the repo's own git
    /// identity and hooks, then reads back what the receipt reports. Only in a worktree that
    /// <see cref="VerifyAsync"/> passes (F6). `commit` is the branch tip when the run moved it; the
    /// changes are then the branch's delta from <see cref="JobWorktreeInfo.BaseCommit"/> (F4), else
    /// <paramref name="snapshot"/>. A git that says no costs a warning, never a throw from here, and
    /// each stage answers for itself (R4): a failure after the commit never reads as "left uncommitted".
    /// Whatever the worktree still holds after the add costs a warning per path, by shape (#74 round 4);
    /// git's `add` stderr goes to <paramref name="addLogPath"/>, and a failure up to the commit clears the index (H5).
    /// </summary>
    public static async Task<BranchReceipt> CommitRunAsync(
        JobWorktreeInfo worktree, string message, SnapshotDiff snapshot, int diffByteCapBytes, string? addLogPath, CancellationToken cancellationToken)
    {
        if (await VerifyAsync(worktree, cancellationToken) is { } mismatch)
            return Unverified(worktree, mismatch, snapshot);

        string? failure;
        string[] leftOut = [];
        try
        {
            (failure, leftOut) = await CommitAllAsync(worktree, message, addLogPath, cancellationToken);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            // Only the status before the add gets here, with nothing left out yet: the commit's own throw is caught inside (J7).
            failure = ex.Message;
        }

        // G3: what was left out no longer holds the rest back, so "left uncommitted" means git said no.
        string[] warnings = [.. leftOut, .. failure is null ? Array.Empty<string>() : [$"work left uncommitted on {worktree.Branch}: {failure}"]];
        string? commit;
        try
        {
            commit = Moved(await BranchTipAsync(worktree, cancellationToken), worktree);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            return new BranchReceipt(Commit: null, snapshot, [.. warnings, DeltaUnavailable($"{worktree.Branch}'s tip could not be read: {ex.Message}")]);
        }

        if (commit is null)
            return new BranchReceipt(Commit: null, snapshot, warnings);

        try
        {
            return await WorktreeSnapshot.BranchDeltaAsync(worktree.Path, worktree.BaseCommit, snapshot, diffByteCapBytes, cancellationToken) is { } delta
                ? new BranchReceipt(commit, delta, warnings)
                : new BranchReceipt(commit, snapshot, [.. warnings, DeltaUnavailable($"git could not diff {worktree.Branch} against {worktree.BaseCommit}")]);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            return new BranchReceipt(commit, snapshot, [.. warnings, DeltaUnavailable(ex.Message)]);
        }
    }

    // A caller with no job directory to log to: git's own `add` line then rides on the left-out warning itself.
    public static async Task<BranchReceipt> CommitRunAsync(JobWorktreeInfo worktree, string message, SnapshotDiff snapshot, int diffByteCapBytes, CancellationToken cancellationToken) =>
        await CommitRunAsync(worktree, message, snapshot, diffByteCapBytes, addLogPath: null, cancellationToken);

    /// <summary>
    /// The receipt of a run whose worktree <see cref="VerifyAsync"/> rejected: nothing committed, no
    /// delta, no `commit`. R5: a path that is no longer the job's worktree reports no changes either —
    /// git would have read them from the main checkout — and a branch whose worktree went away mid-run
    /// may since be another run's (R3), so its tip says nothing about this one. T3: the job's own
    /// worktree — off its branch, mid-operation, or holding unresolved conflicts — keeps <paramref name="snapshot"/>, read inside it.
    /// </summary>
    public static BranchReceipt Unverified(JobWorktreeInfo worktree, WorktreeMismatch mismatch, SnapshotDiff snapshot)
    {
        if (mismatch is { OwnWorktree: true, Operation: { } operation })
            return new BranchReceipt(Commit: null, snapshot, [$"work left uncommitted: {worktree.Path} has {operation} in progress, not a clean {worktree.Branch}"]);
        if (mismatch is { OwnWorktree: true, UnresolvedConflicts: > 0 })
        {
            string conflicts = $"work left uncommitted: {worktree.Path} has unresolved conflicts on {worktree.Branch} — resolve or abort the operation inside the worktree, then commit there yourself";
            return new BranchReceipt(Commit: null, snapshot, [conflicts]);
        }

        if (mismatch.OwnWorktree)
            return new BranchReceipt(Commit: null, snapshot, [$"work left uncommitted: {worktree.Path} is on {mismatch.Reason}, not {worktree.Branch}"]);

        string skipped = $"work left uncommitted: {worktree.Path} is not the job worktree on {worktree.Branch} ({mismatch.Reason})";
        return new BranchReceipt(Commit: null, new SnapshotDiff([], null, false), [skipped]);
    }

    /// <summary>
    /// F6: null when <paramref name="worktree"/>'s path is the top of a working tree whose HEAD is its
    /// branch; otherwise what git found there, or why git could not be asked — a git that fails is an
    /// answer here, not a throw (R5). A directory recreated after its worktree was removed resolves to
    /// the main checkout, where `git add -A` would commit the operator's files. T3: the top of a working
    /// tree on a detached HEAD (a role stopped mid-rebase) or another branch is the job's own worktree,
    /// <see cref="WorktreeMismatch.OwnWorktree"/>: safe to read, never to commit — and so is one on its
    /// branch with a merge, rebase or `git am` in progress, or with unmerged paths (#74 rounds 4 and 5).
    /// </summary>
    public static async Task<WorktreeMismatch?> VerifyAsync(JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree.Path))
            return new WorktreeMismatch("the directory is gone", OwnWorktree: false);

        try
        {
            // An empty prefix, not a path compare: git reports realpaths, and a symlinked or 8.3 cwd as typed never matches.
            (int prefixExit, string prefix, string prefixError) = await GitProcess.RunAsync(worktree.Path, ["rev-parse", "--show-prefix"], cancellationToken);
            if (prefixExit != 0)
                return new WorktreeMismatch(Reason("rev-parse", prefixExit, prefixError, ""), OwnWorktree: false);
            if (prefix.Trim() is { Length: > 0 } inside)
                return new WorktreeMismatch($"git resolves it to {inside} inside another checkout", OwnWorktree: false);

            // `-q` exits 1 for a detached HEAD and only then; anything else is git failing, not an answer.
            (int headExit, string head, string headError) = await GitProcess.RunAsync(worktree.Path, ["symbolic-ref", "-q", "HEAD"], cancellationToken);
            if (headExit == 1)
                return new WorktreeMismatch("a detached HEAD", OwnWorktree: true);
            if (headExit != 0)
                return new WorktreeMismatch(Reason("symbolic-ref", headExit, headError, ""), OwnWorktree: false);
            if (head.Trim() != $"refs/heads/{worktree.Branch}")
                return new WorktreeMismatch(head.Trim(), OwnWorktree: true);

            return await OperationInProgressAsync(worktree.Path, cancellationToken);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            // Removed between the check above and git's start (Win32Exception), or a git past its bound.
            return new WorktreeMismatch(ex.Message, OwnWorktree: false);
        }
    }

    // What the runner's `add -A` and `commit` would conclude, on a HEAD still on the branch (a rebase detaches it).
    // A merge, conflicted or not — the runner never makes a merge commit — a rebase or `git am`, then any unmerged
    // path, whatever left it; a clean `revert -n`/`cherry-pick -n` is an ordinary commit (NOTES.md #74 "Review round 5").
    private static async Task<WorktreeMismatch?> OperationInProgressAsync(string path, CancellationToken cancellationToken)
    {
        // `-q --verify` exits 1 for "no such ref" and only then, and reads the linked worktree's own MERGE_HEAD.
        (int mergeExit, _, string mergeError) = await GitProcess.RunAsync(path, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], cancellationToken);
        if (mergeExit == 0)
            return InProgress("a merge");
        if (mergeExit != 1)
            return new WorktreeMismatch(Reason("rev-parse", mergeExit, mergeError, ""), OwnWorktree: false);

        // Per worktree: relative in the main checkout (`.git/rebase-merge`), absolute in a linked one.
        (int pathsExit, string paths, string pathsError) =
            await GitProcess.RunAsync(path, ["rev-parse", "--git-path", "rebase-merge", "--git-path", "rebase-apply"], cancellationToken);
        if (pathsExit != 0)
            return new WorktreeMismatch(Reason("rev-parse", pathsExit, pathsError, ""), OwnWorktree: false);

        string[] rebaseDirectories = [.. paths.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Path.Combine(path, line.TrimEnd('\r')))];
        if (rebaseDirectories is [_, string apply] && File.Exists(Path.Combine(apply, "applying")))
            return InProgress("a git am");
        if (rebaseDirectories.Any(Directory.Exists))
            return InProgress("a rebase");

        return await UnresolvedConflictsAsync(path, cancellationToken);
    }

    private static WorktreeMismatch InProgress(string operation) => new($"{operation} is in progress", OwnWorktree: true, operation);

    // #74 round 5: a conflicted `merge --squash`, `stash pop`/`apply`, `apply --3way`, cherry-pick or revert leaves no head
    // in common, only unmerged index entries, which `add -A` would resolve — markers and all. `ls-files -u` reads those
    // stages themselves (status's `u`/`UU` agree, measured), which no `status.*` or submodule setting hides.
    // Each `-z` entry is `<mode> <sha> <stage>\t<path>`, one to three per path.
    private static async Task<WorktreeMismatch?> UnresolvedConflictsAsync(string path, CancellationToken cancellationToken)
    {
        (int exitCode, string output, string stderr) = await GitProcess.RunAsync(path, ["ls-files", "-u", "-z"], cancellationToken);
        if (exitCode != 0)
            return new WorktreeMismatch(Reason("ls-files", exitCode, stderr, ""), OwnWorktree: false);

        int paths = output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry[(entry.IndexOf('\t') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .Count();
        return paths == 0
            ? null
            : new WorktreeMismatch($"unresolved conflicts ({paths} {(paths == 1 ? "path" : "paths")})", OwnWorktree: true, UnresolvedConflicts: paths);
    }

    // Read from the main checkout: refs are shared by every worktree, and the job's own may be gone.
    private static async Task<string?> BranchTipAsync(JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        (int exitCode, string stdout, _) = await GitProcess.RunAsync(
            worktree.MainCheckout, ["rev-parse", "--verify", "--quiet", $"refs/heads/{worktree.Branch}"], cancellationToken);
        return exitCode == 0 && stdout.Trim() is { Length: > 0 } sha ? sha : null;
    }

    private static string? Moved(string? tip, JobWorktreeInfo worktree) => tip is not null && tip != worktree.BaseCommit ? tip : null;

    private static string DeltaUnavailable(string reason) => $"receipt delta unavailable, snapshot kept: {reason}";

    // A git that cannot start or passes its bound reads as "nothing committed" — thrown by the first status, the
    // commit's returned beside LeftOut (J7) — NOTES.md "A commit that was made is never reported as left uncommitted"
    // has the limits. LeftOut: a warning per path the add left in the worktree (#74 round 4), which the rest does not wait for.
    private static async Task<(string? Failure, string[] LeftOut)> CommitAllAsync(
        JobWorktreeInfo worktree, string message, string? addLogPath, CancellationToken cancellationToken)
    {
        (int statusExit, string porcelain, string statusError) = await GitProcess.RunAsync(worktree.Path, statusArgs, cancellationToken);
        if (statusExit != 0)
            return (Reason("status", statusExit, statusError, ""), []);
        if (porcelain.Trim().Length == 0)
            return (null, []);

        (string? failure, Staging staging) = await StageAsync(worktree.Path, cancellationToken);
        if (failure is not null)
            return (await ClearIndexAsync(worktree.Path, failure), []);

        (string[] leftOut, bool unexplained) = LeftOutWarnings(worktree, staging, addLogPath);

        // H4: the index decides, not the status — a dirty submodule stages nothing, and git refuses the empty
        // commit ("no changes added to commit", exit 1, measured). Nothing staged for a reason no path's shape
        // explains — a fatal `add`, a stale index.lock — carries git's own line too.
        if (!staging.AnyStaged)
            return (unexplained && staging.AddError.Trim().Length > 0 ? $"git add staged nothing: {GitDiagnostic(staging.AddError)}" : null, leftOut);

        try
        {
            (int commitExit, string commitOutput, string commitError) =
                await GitProcess.RunAsync(worktree.Path, ["commit", "-q", "-m", message], repoCodeTimeout, cancellationToken);
            return (commitExit == 0 ? null : Reason("commit", commitExit, commitError, commitOutput), leftOut);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            // #74 round 5 J7: a hook past the bound fails the commit, not what the add already left out.
            return (ex.Message, leftOut);
        }
    }

    // `add -A`, then the stray gitlinks back out of it, then what the worktree still holds. Failure: what is staged
    // cannot be trusted (H5). AnyStaged is the index after both — `--raw` with `--ignore-submodules=none`, where a
    // plain `diff --cached --quiet` misses a moved pointer under `diff.ignoreSubmodules=all` (measured).
    private static async Task<(string? Failure, Staging Staging)> StageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Exit code and words ignored (#74 round 4): exit 1 is a skipped path, a sparse one or an ignored exclude
            // alike (H2), the words are translated, and the index and the status below say what happened.
            (_, _, string addError) = await GitProcess.RunAsync(
                path, ["--no-literal-pathspecs", "add", "-A", "--ignore-errors", .. allButMachinery], repoCodeTimeout, cancellationToken);

            (string? unreadable, int staged, string[] strays) = await StagedStrayGitlinksAsync(path, cancellationToken);
            if (unreadable is not null)
                return (unreadable, Staging.None);

            if (strays.Length > 0)
            {
                // Literal: a path git printed is a name, never a glob.
                (int resetExit, string resetOutput, string resetError) =
                    await GitProcess.RunAsync(path, ["--literal-pathspecs", "reset", "-q", "--", .. strays], cancellationToken);
                if (resetExit != 0)
                    return ($"an embedded repository at {strays[0]} could not be unstaged: {Reason("reset", resetExit, resetError, resetOutput)}", Staging.None);
            }

            (int remainderExit, string remainder, string remainderError) = await GitProcess.RunAsync(path, remainderArgs, cancellationToken);
            if (remainderExit != 0)
                return (Reason("status", remainderExit, remainderError, ""), Staging.None);

            return (null, new Staging(staged > strays.Length, addError, StatusEntry.Parse(remainder)));
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            return (ex.Message, Staging.None);
        }
    }

    // #74 round 4: a warning per path still unstaged after the add, by shape — git's submodule flag, then a `.git` of
    // its own (a clone, a commit-less `git init`, a hand-made worktree), then anything else: sparse-checkout, an
    // unreadable file, a failing filter, whose cause only git's words give. Unexplained: a path of that last kind.
    private static (string[] Warnings, bool Unexplained) LeftOutWarnings(JobWorktreeInfo worktree, Staging staging, string? addLogPath)
    {
        StatusEntry[] unstaged = [.. staging.Remainder.Where(entry => entry.Unstaged)];
        string[] submodules = [.. unstaged.Where(entry => entry.Submodule).Select(entry => entry.Path)];
        string[] repositories =
            [.. unstaged.Where(entry => !entry.Submodule && Path.Exists(Path.Combine(worktree.Path, entry.Path, ".git"))).Select(entry => entry.Path)];
        string[] others = [.. unstaged.Select(entry => entry.Path).Except([.. submodules, .. repositories], StringComparer.Ordinal)];
        string detail = others.Length == 0 ? "" : AddErrorDetail(worktree, staging.AddError, addLogPath);
        string[] warnings =
        [
            .. repositories.Select(path => $"embedded repository at {path} left out of the commit on {worktree.Branch} — move it out or add it as a submodule"),
            .. submodules.Select(path => $"changes inside submodule {path} left out of the commit on {worktree.Branch} — commit them in the submodule, then stage its pointer"),
            .. others.Select(path => $"{path} left out of the commit on {worktree.Branch}: git did not stage it (sparse-checkout, permissions or a filter{detail})"),
        ];
        return (warnings, others.Length > 0);
    }

    // git's `add` stderr, whole, to the job's stderr.log, which the warning then points at; with no log, or one
    // that cannot be written, its line rides on the warning instead. Nothing said, nothing added.
    private static string AddErrorDetail(JobWorktreeInfo worktree, string addError, string? addLogPath)
    {
        if (addError.Trim().Length == 0)
            return "";

        if (addLogPath is not null)
        {
            try
            {
                File.AppendAllText(addLogPath, $"claustrum: git add -A in {worktree.Path}, for the commit on {worktree.Branch}:{Environment.NewLine}{addError.TrimEnd()}{Environment.NewLine}");
                return " — see the job's stderr.log";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The line below still says something.
            }
        }

        return $" — git said: {GitDiagnostic(addError)}";
    }

    // Display only, never a decision: git's own `fatal:`/`error:` line (prefixes untranslated, measured under it_IT)
    // over an advice banner — "The following paths are ignored …" comes first whenever a machinery directory exists.
    private static string GitDiagnostic(string stderr)
    {
        string[] lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line => line.StartsWith("fatal: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal))
            ?? lines.FirstOrDefault()
            ?? "";
    }

    // H5: past `add`, a failure leaves an index nobody checked — a stray gitlink in it — and the warning's
    // remedy, "commit it inside that worktree yourself", would commit it. So it is reset whole (the work stays
    // in the worktree), never cancelled: this is the cleanup, like UndoAddAsync's.
    private static async Task<string> ClearIndexAsync(string path, string failure)
    {
        try
        {
            (int exitCode, string stdout, string stderr) = await GitProcess.RunAsync(path, ["reset", "-q"], CancellationToken.None);
            return exitCode == 0
                ? $"{failure} (index cleared: nothing is staged)"
                : $"{failure} (and the index could not be cleared: {Reason("reset", exitCode, stderr, stdout)})";
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            return $"{failure} (and the index could not be cleared: {ex.Message})";
        }
    }

    // #74 F1's second net: a directory with its own `.git` outside the machinery — a clone, a hand-made worktree —
    // stages as a new `160000` gitlink, a sha nobody can check out unless the staged `.gitmodules` lists its path
    // (G3: then it is a deliberate submodule). `--ignore-submodules=none` (G2): under `diff.ignoreSubmodules=all`
    // this diff prints no gitlink at all (measured 2026-10-09, git 2.56). Each `--raw -z` entry is
    // `:<old mode> <new mode> <old sha> <new sha> <status>`, then the path. Staged counts every entry.
    private static async Task<(string? Failure, int Staged, string[] Strays)> StagedStrayGitlinksAsync(string path, CancellationToken cancellationToken)
    {
        (int exitCode, string raw, string stderr) =
            await GitProcess.RunAsync(path, ["diff", "--cached", "--raw", "-z", "--no-renames", "--ignore-submodules=none"], cancellationToken);
        if (exitCode != 0)
            return (Reason("diff --cached", exitCode, stderr, ""), 0, []);

        string[] fields = raw.Split('\0');
        (string Modes, string Path)[] entries = [.. Enumerable.Range(0, fields.Length / 2).Select(index => (fields[2 * index], fields[2 * index + 1]))];
        string[] added = [.. entries.Where(entry => entry.Modes.Split(' ') is [not ":160000", "160000", ..]).Select(entry => entry.Path)];
        if (added.Length == 0)
            return (null, entries.Length, []);

        string[] submodules = await StagedSubmodulePathsAsync(path, cancellationToken);
        return (null, entries.Length, [.. added.Where(gitlink => !submodules.Contains(gitlink, StringComparer.Ordinal))]);
    }

    // `--blob :.gitmodules` reads the index, where `add -A` has just put the worktree's copy. `-z` ends each
    // `<key>\n<value>`: a submodule's name and path may hold spaces. No file or no key exits 1, so none.
    private static async Task<string[]> StagedSubmodulePathsAsync(string path, CancellationToken cancellationToken)
    {
        (int exitCode, string output, _) = await GitProcess.RunAsync(
            path, ["config", "--blob", ":.gitmodules", "-z", "--get-regexp", @"^submodule\..*\.path$"], cancellationToken);
        return exitCode != 0 ? [] : [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry[(entry.IndexOf('\n') + 1)..])];
    }

    // F9: provably nothing of the run's in it — so `--force` and deleting the branch lose nothing.
    private static async Task<bool> IsUntouchedAsync(JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        if (NestedWorktrees(worktree.Path) is not null)
            return false;
        if (await VerifyAsync(worktree, cancellationToken) is not null || await BranchTipAsync(worktree, cancellationToken) != worktree.BaseCommit)
            return false;

        (int exitCode, string porcelain, _) = await GitProcess.RunAsync(worktree.Path, statusArgs, cancellationToken);
        return exitCode == 0 && porcelain.Trim().Length == 0;
    }

    // `-q`: without it git's first stderr line is "Preparing worktree (…)", not the reason (git 2.56).
    // Whatever stops the add — git's own no, a post-checkout hook exiting non-zero (which leaves a whole
    // worktree, measured 2026-10-08), the bound, a cancel — what it made is undone before the reason is
    // returned or the cancel rethrown (T1).
    private static async Task<string?> TryAddAsync(string cwd, JobWorktreeInfo worktree, string[] target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "worktrees"));

        // #108, found by review 2026-10-14: the directory is made here, exclusively, so the undo deletes only what this add
        // made. Refused before git runs when someone else's is already there.
        if (CreateExclusively(worktree.Path) is { } notMade)
            return notMade;

        string reason;
        try
        {
            (int exitCode, string stdout, string stderr) =
                await GitProcess.RunAsync(cwd, ["worktree", "add", "-q", worktree.Path, .. target], repoCodeTimeout, cancellationToken);

            if (exitCode == 0)
                return null;

            reason = Reason("worktree add", exitCode, stderr, stdout);
        }
        catch (OperationCanceledException ex)
        {
            // Still a cancel, so `run` exits 130; what the undo could not remove rides on the message. Since
            // #94 (2026-10-13) that message is the cancelled receipt's `error`, not a RunCommand.Cancelled line.
            if (await UndoAddAsync(cwd, worktree) is { } left)
                throw new OperationCanceledException($"git worktree add was cancelled; left behind: {left}", ex, ex.CancellationToken);
            throw;
        }
        catch (TimeoutException)
        {
            string timedOut = $"git worktree add did not finish within {repoCodeTimeout.TotalMinutes} minutes";
            return await UndoAddAsync(cwd, worktree) is { } left
                ? $"{timedOut}; left behind: {left}"
                : $"{timedOut}; nothing left behind";
        }

        return await UndoAddAsync(cwd, worktree) is { } leftover ? $"{reason}; left behind: {leftover}" : reason;
    }

    // `Directory.Move` fails when the target exists (measured on Linux for an empty directory, a full one, a file, a link, a
    // dangling link), so a staging directory moved onto the job's path proves this add made it; the microseconds between .NET's
    // check and rename(2) can only replace an empty directory, which holds nothing. Git accepts an existing empty directory.
    // The `.trash-` name lets `jobs clean` sweep a staging directory a kill left. Null when made, else why not.
    private static string? CreateExclusively(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        if (Path.GetDirectoryName(trimmed) is not { } parent)
            return $"{path} has no parent directory";

        string staging = Path.Combine(parent, $"{TrashPrefix}{Path.GetFileName(trimmed)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        try
        {
            Directory.CreateDirectory(staging);
            Directory.Move(staging, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                Directory.Delete(staging);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Empty, and swept by the next `jobs clean`.
            }

            return $"the job directory {path} could not be created exclusively ({ex.Message})";
        }
    }

    // T1: the role never ran, so nothing in the worktree is anyone's. `-f -f`, because git killed
    // mid-checkout leaves the entry `locked initializing`, which `remove` and `remove --force` refuse.
    // Only after a failed remove, the run's own entry is released (ReleaseDeadEntryAsync): one whose `.git` file is
    // gone fails the remove and stays registered (both measured, git 2.56). Never a repo-wide prune (review 2026-10-14).
    // The bound is the checkout's: deleting a large tree is as slow as writing it. Null when nothing is left.
    // #108: a directory git left unregistered (killed before it wrote the entry) is deleted too, see TryDeleteUnregisteredAsync.
    private static async Task<string?> UndoAddAsync(string cwd, JobWorktreeInfo worktree)
    {
        try
        {
            (int exitCode, string stdout, string stderr) =
                await GitProcess.RunAsync(cwd, ["worktree", "remove", "--force", "--force", worktree.Path], repoCodeTimeout, CancellationToken.None);
            (_, string? entryFailure) = exitCode == 0 ? (false, null) : await ReleaseDeadEntryAsync(cwd, worktree.Path, directoryGone: false, CancellationToken.None);

            List<string> left = [];
            if (Directory.Exists(worktree.Path))
            {
                if (await TryDeleteUnregisteredAsync(cwd, worktree.Path) is { } stays)
                    left.Add($"{worktree.Path} ({Reason("worktree remove", exitCode, stderr, stdout)}; not deleted: {stays})");
            }

            if (entryFailure is not null)
                left.Add($"the registration of {worktree.Path} ({entryFailure})");
            if (await TryDeleteOwnedBranchAsync(cwd, worktree, CancellationToken.None) is { } branchFailure)
                left.Add($"branch {worktree.Branch} ({branchFailure})");

            return left.Count == 0 ? null : string.Join(" and ", left);
        }
        catch (Exception ex) when (IsGitFailure(ex) || ex is WorktreeInterruptedException)
        {
            string branch = worktree.OwnsBranch ? $" and branch {worktree.Branch}" : "";
            return $"possibly {worktree.Path}{branch} (undoing stopped: {ex.Message})";
        }
    }

    // #108: git killed before it registered the worktree leaves a directory `remove` calls "not a working tree". The role
    // never ran in it, so it holds nothing of anyone's: deleted when it is a job directory of this repository, no link
    // itself, and git (asked for the typed and the realpath spelling, as ReleaseDeadEntryAsync does) lists no worktree
    // there. Directory.Delete does not follow links inside it, junctions included. Null when deleted, else why not.
    private static async Task<string?> TryDeleteUnregisteredAsync(string cwd, string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        string name = Path.GetFileName(trimmed);
        string[] jobsDirectories = await JobsDirectoriesAsync(cwd, CancellationToken.None);
        if (name.Length == 0 || Path.GetDirectoryName(trimmed) is not { } parent || !jobsDirectories.Any(directory => SamePath(directory, parent)))
            return "it is not a job directory of this repository's .claustrum/worktrees";
        if (new DirectoryInfo(path).LinkTarget is not null)
            return "it is a link";
        if (await ListWorktreesAsync(cwd, CancellationToken.None) is not { } listed)
            return "git could not list the worktrees";

        string[] spellings = [.. jobsDirectories.Select(directory => Path.Combine(directory, name))];
        if (listed.Any(entry => spellings.Any(spelling => SamePath(spelling, entry.Path))))
            return "git lists it as a worktree";

        try
        {
            Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    // F9's rule, shared by both undos: only a branch the run created (`-b`) and still at BaseCommit is
    // deleted. -D, not -d: it was cut from HEAD and never merged anywhere, so git's merged-check would
    // refuse the plain delete every time. Null when deleted, or not this run's to delete.
    private static async Task<string?> TryDeleteOwnedBranchAsync(string cwd, JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        if (!worktree.OwnsBranch || await BranchTipAsync(worktree, cancellationToken) != worktree.BaseCommit)
            return null;

        (int exitCode, string stdout, string stderr) = await GitProcess.RunAsync(cwd, ["branch", "-D", worktree.Branch], cancellationToken);
        return exitCode == 0 ? null : Reason("branch -D", exitCode, stderr, stdout);
    }

    // Only a worktree Claustrum just made and that provably holds nothing (TryRemoveAbandonedAsync). Null when
    // git removed it; otherwise why not.
    private static async Task<string?> TryForceRemoveAsync(string cwd, string path)
    {
        (int exitCode, string stdout, string stderr) =
            await GitProcess.RunAsync(cwd, ["worktree", "remove", "--force", path], repoCodeTimeout, CancellationToken.None);
        return exitCode == 0 ? null : Reason("worktree remove", exitCode, stderr, stdout);
    }

    // #106: frees a finished job's worktree without `git worktree remove`, whose half-done deletion a Ctrl-C leaves
    // registered. Refuse unless it is clean (checks below, Claustrum's own: git's honours `status.showUntrackedFiles=no`
    // and deletes new files silently, R2); rename it aside — atomic, so a kill leaves it whole or gone; delete its own
    // admin entry, never a repo-wide prune (review 2026-10-14: that dropped an operator's unmounted worktree); delete the
    // trash. Refusal: why it stays. Warning: freed, but something is left (trash, or an entry git still lists).
    // NOTES.md T1 "A cancel during the free" has the failure matrix, and why the earlier marker was dropped.
    private static async Task<(string? Refusal, string? Warning)> TryFreeAsync(string cwd, string path, CancellationToken cancellationToken)
    {
        (string? refusal, string top, string admin) = await CheckFreeableAsync(cwd, path, cancellationToken);
        if (refusal is not null)
            return (refusal, null);

        // The worktrees directory of #104's fence: the trash is its sibling, so it is no further out than the worktree.
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not { } parent)
            return ("it has no parent directory", null);

        string trash = Path.Combine(parent, $"{TrashPrefix}{Path.GetFileName(path)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        try
        {
            Directory.Move(path, trash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"it could not be moved aside, so it is intact ({ex.Message})", null);
        }

        try
        {
            if (await ReleaseAsync(cwd, top, admin, cancellationToken) is { } stuck)
                return (null, $"{path} freed, but {stuck} (trash at {trash}; `claustrum jobs clean` removes it)");
        }
        catch (OperationCanceledException ex)
        {
            string message = $"{ex.Message}; {path} is already freed, its trash at {trash}, which `claustrum jobs clean` removes";
            throw ex is WorktreeInterruptedException
                ? new WorktreeInterruptedException(message)
                : new OperationCanceledException(message, ex, ex.CancellationToken);
        }

        return (null, DeleteTrash(path, trash));
    }

    // The renamed-away worktree's admin entry, and only it: git has no scoped prune, and a repo-wide one also drops the
    // operator's worktrees whose directory is merely unmounted. Null when `top` is no longer listed. A failure is what
    // is left behind, for the warning; a git past its bound reads as one, but a cancel throws.
    private static async Task<string?> ReleaseAsync(string cwd, string top, string admin, CancellationToken cancellationToken)
    {
        if (DeleteAdmin(admin) is { } failure)
            return failure;

        try
        {
            if (await ListWorktreesAsync(cwd, cancellationToken) is not { } listed)
                return "`git worktree list` failed afterwards, so its registration could not be confirmed gone";

            return listed.Any(entry => SamePath(entry.Path, top)) ? "git still lists it as a worktree" : null;
        }
        catch (TimeoutException ex)
        {
            return $"{ex.Message}, so its registration could not be confirmed gone";
        }
    }

    // Best effort and under no token: it holds nothing (the checks proved it clean), so a failure or a kill costs only disk.
    private static string? DeleteTrash(string path, string trash)
    {
        try
        {
            Directory.Delete(trash, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path} freed, but {trash} could not be deleted ({ex.Message}) — `claustrum jobs clean` sweeps it";
        }
    }

    /// <summary>
    /// #106, `jobs clean`: deletes every <c>.trash-*</c> a free left under <c>.claustrum/worktrees</c> that git does
    /// not list as a worktree (review 2026-10-14: a registered one is somebody's), after dropping the one admin entry
    /// each may still have — named by the trash's own `.git`, never a repo-wide prune, which would also unregister the
    /// operator's unmounted worktrees. A failed delete is a returned message, never a throw.
    /// </summary>
    public static async Task<(int Swept, string[] Failures)> SweepTrashAsync(string cwd, CancellationToken cancellationToken)
    {
        string root = Path.Combine(cwd, ".claustrum", "worktrees");
        if (!Directory.Exists(root))
            return (0, []);

        string[] trashes = [.. Directory.EnumerateDirectories(root, $"{TrashPrefix}*")];
        if (trashes.Length == 0)
            return (0, []);

        WorktreeEntry[]? listed;
        try
        {
            listed = await ListWorktreesAsync(cwd, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception)
        {
            return (0, [$"git could not list the worktrees ({ex.Message}), so no trash was swept"]);
        }

        if (listed is null)
            return (0, ["git could not list the worktrees, so no trash was swept"]);

        int swept = 0;
        List<string> failures = [];
        foreach (string trash in trashes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // By its tail, case-insensitively, as TryRemoveStrayProbeAsync does: git lists a realpath, and a false match only keeps the directory.
            string tail = $"/.claustrum/worktrees/{Path.GetFileName(trash)}";
            if (listed.Any(entry => entry.Path.Replace('\\', '/').EndsWith(tail, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{trash}: git lists it as a worktree, so it is not trash — left in place");
                continue;
            }

            try
            {
                if (await ReleaseTrashEntryAsync(cwd, trash, cancellationToken) is { } stuck)
                {
                    failures.Add($"{trash}: {stuck}");
                    continue;
                }

                Directory.Delete(trash, recursive: true);
                swept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or Win32Exception)
            {
                failures.Add($"{trash}: {ex.Message}");
            }
        }

        return (swept, [.. failures]);
    }

    // A free killed after its rename but before it dropped the entry leaves one pointing at a directory that is gone.
    // The trash's `.git` names it; it is deleted only when it is this repository's, unlocked, names a top that no longer
    // exists, that lived in this jobs directory and whose name this trash was made from (review 2026-10-14: a planted
    // trash must not unregister another worktree). Every file is read through PlainFile, so a FIFO is a report, not a hang.
    // No entry there: nothing to release (null). An entry that is not released is a report and the trash stays: it is the
    // only pointer to it (round 3). Else why not.
    private static async Task<string?> ReleaseTrashEntryAsync(string cwd, string trash, CancellationToken cancellationToken)
    {
        string dotGit = Path.Combine(trash, ".git");
        if (!File.Exists(dotGit))
            return null;
        if (!TryRead(dotGit, out string? text, out string? refusal))
            return $"{dotGit} was not read ({refusal}), so its registration was not looked for";
        if (text.Trim() is not ['g', 'i', 't', 'd', 'i', 'r', ':', .. { } target] || target.Trim() is not { Length: > 0 } entry)
            return null;

        string admin = Path.GetFullPath(entry, trash);
        if (!File.Exists(Path.Combine(admin, "gitdir")))
            return null;
        if (!TryReadGitdir(admin, out string? entryDotGit, out string? gitdirRefusal))
            return $"{Path.Combine(admin, "gitdir")} was not read ({gitdirRefusal}), so its registration was left";

        string[] jobsDirectories = await JobsDirectoriesAsync(cwd, cancellationToken);
        string? stays = await TrashEntryStaysAsync(cwd, trash, admin, entryDotGit, jobsDirectories, cancellationToken);
        return stays is null
            ? DeleteAdmin(admin)
            : $"its .git names the registration {admin}, which was not released ({stays}); the trash is kept as the only pointer to it";
    }

    // Why the entry `admin` of a trash is not this free's to delete; null when it is.
    private static async Task<string?> TrashEntryStaysAsync(
        string cwd, string trash, string admin, string entryDotGit, string[] jobsDirectories, CancellationToken cancellationToken)
    {
        if (Path.GetDirectoryName(entryDotGit) is not { } top)
            return "it names no directory";
        if (Path.Exists(top) || new FileInfo(top).LinkTarget is not null)
            return $"its worktree {top} still exists";
        if (Path.GetDirectoryName(top) is not { } parent || !jobsDirectories.Any(directory => SamePath(directory, parent)))
            return $"its worktree {top} was not in this repository's .claustrum/worktrees";
        if (!Path.GetFileName(trash).StartsWith($"{TrashPrefix}{Path.GetFileName(top)}-", StringComparison.Ordinal))
            return $"its worktree {top} is not the one this trash was made from";
        if (File.Exists(Path.Combine(admin, "locked")))
            return "it is locked (`git worktree lock`)";
        if (Path.GetFileName(Path.GetDirectoryName(admin)) != "worktrees")
            return "it is not an entry of a `worktrees` directory";

        // Both from git, so a symlinked cwd, a subst drive or an 8.3 name cannot make one repository look like two.
        (string? ours, string? refusal) = await CommonDirAsync(cwd, cancellationToken);
        if (ours is null)
            return refusal;

        (int exitCode, string output, string stderr) = await FreeGitAsync(cwd, ["--git-dir", admin, "rev-parse", PathFormatFlag, "--git-common-dir"], cancellationToken);
        if (exitCode != 0 || output.Trim() is not { Length: > 0 } theirs || output.Contains(PathFormatFlag, StringComparison.Ordinal))
            return $"git could not name its repository ({Reason("rev-parse", exitCode, stderr, "")})";

        return SamePath(ours, theirs) ? null : $"it belongs to another repository ({theirs})";
    }

    // The entry of a worktree Claustrum made, and only it: `git worktree prune` is repo-wide and also drops an operator's
    // unmounted worktree (review 2026-10-14). So the one admin dir whose `gitdir` names `<top>/.git` is deleted, when `top`
    // lies in this repository's `.claustrum/worktrees` and the entry is not locked (as prune). `directoryGone`: the caller
    // holds work in `top` unless the directory itself is gone (a branch's holder, round 3); else the role never ran and a
    // missing `.git` is enough (a failed add). Released: whether it was; Failure: why a release that was ours failed.
    private static async Task<(bool Released, string? Failure)> ReleaseDeadEntryAsync(string cwd, string top, bool directoryGone, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(top));
        bool dead = directoryGone ? !Path.Exists(top) && new FileInfo(top).LinkTarget is null : !Path.Exists(Path.Combine(top, ".git"));
        if (name.Length == 0 || !dead)
            return (false, null);

        // git records a realpath, so the jobs directory is compared as typed and as git resolves it.
        string[] jobsDirectories = await JobsDirectoriesAsync(cwd, cancellationToken);
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(top)) is not { } parent || !jobsDirectories.Any(directory => SamePath(directory, parent)))
            return (false, null);

        (string? common, string? refusal) = await CommonDirAsync(cwd, cancellationToken);
        if (common is null)
            return (false, refusal);

        if (FindAdmin(common, [top, .. jobsDirectories.Select(directory => Path.Combine(directory, name))]) is not { } admin || File.Exists(Path.Combine(admin, "locked")))
            return (false, null);

        return DeleteAdmin(admin) is { } failure ? (false, failure) : (true, null);
    }

    // `.claustrum/worktrees` of `cwd` as typed, then as git spells it (toplevel + prefix): git lists realpaths.
    private static async Task<string[]> JobsDirectoriesAsync(string cwd, CancellationToken cancellationToken)
    {
        string typed = Path.Combine(cwd, ".claustrum", "worktrees");
        if (!Directory.Exists(typed))
            return [typed];

        (int exitCode, string output, _) = await FreeGitAsync(typed, ["rev-parse", "--show-toplevel", "--show-prefix"], cancellationToken);
        string[] lines = [.. output.Split('\n').Select(line => line.TrimEnd('\r'))];
        return exitCode == 0 && lines is [{ Length: > 0 } toplevel, { } prefix, ..] ? [typed, Path.Combine(toplevel, prefix)] : [typed];
    }

    // The `<common>/worktrees/<id>` whose `gitdir` names the `.git` of one of `tops`; null when none does.
    private static string? FindAdmin(string common, string[] tops)
    {
        string entries = Path.Combine(common, "worktrees");
        try
        {
            return Directory.Exists(entries)
                ? Directory.EnumerateDirectories(entries).FirstOrDefault(admin =>
                    TryReadGitdir(admin, out string? dotGit, out _) && tops.Any(top => SamePath(dotGit, Path.Combine(top, ".git"))))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? DeleteAdmin(string admin)
    {
        try
        {
            Directory.Delete(admin, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"its registration {admin} could not be deleted ({ex.Message})";
        }
    }

    // `rev-parse --path-format=absolute` is git 2.31's; an older git echoes the flag back as if it were an answer.
    private const string PathFormatFlag = "--path-format=absolute";
    private const string GitTooOld = "it needs git 2.31 or later (this git does not know `rev-parse --path-format=absolute`)";

    private static async Task<(string? Common, string? Refusal)> CommonDirAsync(string cwd, CancellationToken cancellationToken)
    {
        (int exitCode, string output, string stderr) = await FreeGitAsync(cwd, ["rev-parse", PathFormatFlag, "--git-common-dir"], cancellationToken);
        if (exitCode == 0 && output.Contains(PathFormatFlag, StringComparison.Ordinal))
            return (null, GitTooOld);

        return exitCode == 0 && output.Trim() is { Length: > 0 } common
            ? (common, null)
            : (null, $"git could not find the repository of {cwd} ({Reason("rev-parse", exitCode, stderr, "")})");
    }

    // A worktree's own files through PlainFile: a FIFO planted in one is refused, not opened (it would block).
    private static bool TryRead(string path, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? refusal)
    {
        try
        {
            if (!PlainFile.TryReadAllText(path, out text, out refusal))
                return false;

            // A NUL makes Path.GetFullPath throw, which aborted `jobs clean` on every run (review 2026-10-14): not read.
            if (!text.Contains('\0'))
                return true;

            text = null;
            refusal = "it holds a NUL byte";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = null;
            refusal = ex.Message;
            return false;
        }
    }

    // The `.git` file an admin entry's `gitdir` names, absolute. With `worktree.useRelativePaths` (git 2.48) it is written
    // relative to the entry, and resolving it against the process directory instead refused every free (review 2026-10-14).
    private static bool TryReadGitdir(string admin, [NotNullWhen(true)] out string? dotGit, [NotNullWhen(false)] out string? refusal)
    {
        dotGit = null;
        if (!TryRead(Path.Combine(admin, "gitdir"), out string? text, out refusal))
            return false;

        if (text.TrimEnd('\r', '\n') is not { Length: > 0 } line)
        {
            refusal = "it is empty";
            return false;
        }

        dotGit = Path.GetFullPath(line, admin);
        return true;
    }

    // The sibling prefix of a worktree renamed aside; `jobs clean` sweeps it and must not read one as a job.
    private const string TrashPrefix = ".trash-";

    public static bool IsTrash(string directoryName) => directoryName.StartsWith(TrashPrefix, StringComparison.Ordinal);

    // Every condition under which the worktree at `path` may be renamed away; Top is git's realpath of it, Admin its
    // entry under the common dir. All of them hold or it is today's refusal, unchanged: a dirty one is work nobody looked
    // at (F3), a populated submodule may hold commits pushed nowhere (#74 J4), a locked one is somebody's (`git worktree
    // lock`). Review 2026-10-14 added: not this repository's worktree, a detached HEAD or mid-operation, commits only its
    // reflog holds, edits hidden by a bit.
    private static async Task<(string? Refusal, string Top, string Admin)> CheckFreeableAsync(string cwd, string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return ("it is not a directory", "", "");
        if (new DirectoryInfo(path).LinkTarget is not null)
            return ("it is a symbolic link", "", "");

        try
        {
            // The prefix is empty at a working tree's top; a plain directory inside the main checkout resolves to it.
            (int exitCode, string output, string stderr) =
                await FreeGitAsync(path, ["rev-parse", "--path-format=absolute", "--show-prefix", "--show-toplevel", "--git-dir", "--git-common-dir"], cancellationToken);
            string[] lines = [.. output.Split('\n').Select(line => line.TrimEnd('\r'))];
            if (exitCode == 0 && output.Contains(PathFormatFlag, StringComparison.Ordinal))
                return (GitTooOld, "", "");
            if (exitCode != 0 || lines.Length < 4)
                return ($"git finds no working tree there ({Reason("rev-parse", exitCode, stderr, "")})", "", "");
            if (lines[0].Length > 0)
                return ($"git resolves it to {lines[0]} inside another checkout, not a worktree of its own", "", "");

            // A role that ran `mv .git x; git init` leaves a directory the admin entry still lists, which git's own
            // `worktree remove` would refuse: its repository is the role's, so its commits are in there only.
            string top = lines[1];
            string admin = lines[2];
            (string? ourCommon, string? commonRefusal) = await CommonDirAsync(cwd, cancellationToken);
            if (ourCommon is null)
                return (commonRefusal, "", "");

            if (!SamePath(lines[3], ourCommon)
                || Path.GetDirectoryName(Normalize(admin)) is not { } adminParent
                || !SamePath(adminParent, Path.Combine(lines[3], "worktrees"))
                || !AdminNames(admin, top))
            {
                return ($"it is not this repository's worktree: its .git leads to {admin}, not to an entry of {ourCommon} naming it", "", "");
            }

            // Past the first entry, which is the main checkout: git lists a worktree by its realpath.
            if (await ListWorktreesAsync(cwd, cancellationToken) is not { } listed)
                return ("git could not list the worktrees", "", "");
            if (listed.Skip(1).FirstOrDefault(entry => SamePath(entry.Path, top)) is not { } entry)
                return ($"it is not a linked worktree of {cwd}", "", "");
            if (entry.Locked)
                return ("it is locked (`git worktree lock`)", "", "");

            string? unsafeToMove = await OffBranchAsync(path, admin, cancellationToken)
                ?? await DanglingReflogAsync(path, cancellationToken)
                ?? NestedWorktrees(path)
                ?? await UncommittedAsync(path, cancellationToken)
                ?? await HiddenEditsAsync(path, cancellationToken);
            if (unsafeToMove is null && await HoldsPopulatedSubmoduleAsync(path, cancellationToken))
                unsafeToMove = $"it holds a populated submodule, whose commits may exist nowhere else — remove it by hand (`git worktree remove --force \"{path}\"`)";

            return (unsafeToMove, top, admin);
        }
        catch (Win32Exception)
        {
            return ("it was removed while it was being checked", "", "");
        }
    }

    // The admin entry's `gitdir` file holds the `.git` file of the worktree it belongs to.
    private static bool AdminNames(string admin, string top) =>
        TryReadGitdir(admin, out string? dotGit, out _) && SamePath(dotGit, Path.Combine(top, ".git"));

    // Per-worktree state in the admin dir that only a person can settle; `sequencer` is a multi-pick's, which keeps
    // CHERRY_PICK_HEAD only between picks.
    private static readonly (string Name, string Operation)[] operationMarkers =
    [
        ("rebase-merge", "a rebase"), ("rebase-apply", "a rebase or git am"), ("MERGE_HEAD", "a merge"), ("CHERRY_PICK_HEAD", "a cherry-pick"),
        ("sequencer", "a cherry-pick or revert sequence"), ("REVERT_HEAD", "a revert"), ("BISECT_LOG", "a bisect"),
    ];

    // Commits on a detached HEAD, and anything a rebase, merge, cherry-pick or the like holds, are reachable from this
    // directory only once it goes — review 2026-10-14. A commit on any existing branch lives in the shared refs, so whichever
    // branch HEAD names is fine (later in that day's review: a `--branch` job is on its own given branch). Null then.
    private static async Task<string?> OffBranchAsync(string path, string admin, CancellationToken cancellationToken)
    {
        if (operationMarkers.FirstOrDefault(marker => Path.Exists(Path.Combine(admin, marker.Name))) is { Operation: { } operation })
            return $"it has {operation} in progress — finish or abort it inside the worktree, then retry";

        (int exitCode, string head, string stderr) = await FreeGitAsync(path, ["symbolic-ref", "-q", "HEAD"], cancellationToken);

        // `-q` exits 1 for a detached HEAD and only then; anything else is git failing, not an answer.
        if (exitCode == 1)
            return "it is on a detached HEAD, whose commits belong to no branch — put them on a branch (or discard them) inside the worktree, then retry";
        if (exitCode != 0)
            return Reason("symbolic-ref", exitCode, stderr, "");

        // `show-ref --verify --quiet` exits 1 for a ref that is not there: an unborn branch holds no ref for its commits.
        string name = head.Trim();
        if (!name.StartsWith("refs/heads/", StringComparison.Ordinal))
            return $"its HEAD names {name}, which is not a branch, so commits made there belong to no branch — switch to a branch inside the worktree, then retry";

        (int refExit, _, string refError) = await FreeGitAsync(path, ["show-ref", "--verify", "--quiet", name], cancellationToken);
        return refExit switch
        {
            0 => null,
            1 => $"its HEAD names {name}, which does not exist, so commits made there belong to no branch — create it or switch to a branch inside the worktree, then retry",
            _ => Reason("show-ref", refExit, refError, ""),
        };
    }

    // Commits the worktree's own HEAD reflog holds, or a ref of its own (refs/worktree, bisect, rewritten: the admin dir
    // goes too), that no shared ref and no shared reflog reaches die with its admin dir. `rev-list --parents <those> --not
    // <shared refs>` keeps the ones outside every ref; the branch's reflog survives the free (it lives in the common dir), so
    // what it reaches is not lost; one the branch carries again under another sha (`git cherry`: the same patch, as after the
    // documented rebase-then-fast-forward recipe) is not either. NOTES.md T1 "Remediation, round 2" and "round 3".
    private static async Task<string?> DanglingReflogAsync(string path, CancellationToken cancellationToken)
    {
        // `rev-list -g`, not `log -g`: `log.showSignature` puts gpg lines in the latter's output (review 2026-10-14).
        (int logExit, string log, string logError) = await FreeGitAsync(path, ["rev-list", "--walk-reflogs", "HEAD"], cancellationToken);
        if (logExit != 0)
            return Reason("rev-list -g", logExit, logError, "");

        (int refsExit, string ownRefs, string refsError) = await FreeGitAsync(path, ["for-each-ref", "--format=%(objectname)", .. worktreeRefNamespaces], cancellationToken);
        if (refsExit != 0)
            return Reason("for-each-ref", refsExit, refsError, "");

        // sha → parents. The shas go in chunks, because a Windows command line is 32 KB.
        Dictionary<string, string[]> dangling = [];
        foreach (string[] chunk in ObjectIds(log).Concat(ObjectIds(ownRefs)).Distinct().Chunk(100))
        {
            (int exitCode, string output, string stderr) = await FreeGitAsync(path, ["rev-list", "--parents", .. chunk, "--not", .. sharedRefs, "--"], cancellationToken);
            if (exitCode != 0)
                return Reason("rev-list", exitCode, stderr, "");

            foreach (string[] fields in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Split(' ')))
                dangling[fields[0]] = fields[1..];
        }

        if (dangling.Count > 0)
        {
            (string? reachedRefusal, string[] reached) = await BranchReflogReachesAsync(path, cancellationToken);
            if (reachedRefusal is not null)
                return reachedRefusal;

            foreach (string sha in reached)
                dangling.Remove(sha);
        }

        // `cherry` marks `- <sha>` a commit whose patch the branch holds since their merge base; a merge is never listed, so it stays lost.
        string[] tips = [.. dangling.Keys.Where(sha => !dangling.Values.Any(parents => parents.Contains(sha)))];
        HashSet<string> superseded = [];
        foreach (string tip in tips.Take(MaxCherryTips))
        {
            (int exitCode, string output, _) = await FreeGitAsync(path, ["cherry", "HEAD", tip], cancellationToken);
            if (exitCode == 0)
                superseded.UnionWith(output.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..].Trim()));
        }

        string[] lost = [.. dangling.Keys.Where(sha => !superseded.Contains(sha))];
        return lost.Length == 0
            ? null
            : $"its HEAD reflog or refs of its own hold {lost.Length} commit{(lost.Length == 1 ? "" : "s")} that no branch, tag or other ref contains (e.g. {lost[0]}), "
                + $"which the free would lose — keep them with `git -C \"{path}\" branch <name> {lost[0]}`, or drop them with "
                + $"`git -C \"{path}\" reflog expire --expire-unreachable=now HEAD` (and `git update-ref -d` for a refs/worktree, refs/bisect or refs/rewritten one), then retry";
    }

    // Per-worktree ref namespaces: they live in the admin dir, so `--all` run in the worktree must not count them as safe.
    private static readonly string[] worktreeRefNamespaces = ["refs/worktree", "refs/bisect", "refs/rewritten"];

    // The `--not` side: every shared ref (and HEAD, which names an existing branch by now), none of the worktree's own.
    // The excludes come before `--all`, which is what they apply to.
    private static readonly string[] sharedRefs = [.. worktreeRefNamespaces.Select(name => $"--exclude={name}/*"), "--all"];

    // Only what is a hex object id: a stray line (a gpg message, a warning) is never a revision.
    private static IEnumerable<string> ObjectIds(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length is 40 or 64 && line.All(char.IsAsciiHexDigit));

    // The commits (outside every ref) the BRANCH's reflog reaches. That reflog is shared, so it survives the free: an amend or a
    // reset made while on the branch leaves its old commit there. Empty when HEAD names no branch (OffBranchAsync has refused it).
    private static async Task<(string? Refusal, string[] Shas)> BranchReflogReachesAsync(string path, CancellationToken cancellationToken)
    {
        (int headExit, string head, _) = await FreeGitAsync(path, ["symbolic-ref", "-q", "HEAD"], cancellationToken);
        if (headExit != 0)
            return (null, []);

        (int logExit, string log, string logError) = await FreeGitAsync(path, ["rev-list", "--walk-reflogs", head.Trim()], cancellationToken);
        if (logExit != 0)
            return (Reason("rev-list -g", logExit, logError, ""), []);

        HashSet<string> reached = [];
        foreach (string[] chunk in ObjectIds(log).Distinct().Chunk(100))
        {
            (int exitCode, string output, string stderr) = await FreeGitAsync(path, ["rev-list", .. chunk, "--not", .. sharedRefs, "--"], cancellationToken);
            if (exitCode != 0)
                return (Reason("rev-list", exitCode, stderr, ""), []);

            reached.UnionWith(ObjectIds(output));
        }

        return (null, [.. reached]);
    }

    // Past this many separate dangling chains the patch comparison is skipped and they all count as lost.
    private const int MaxCherryTips = 20;

    // `skip-worktree` and `assume-unchanged` hide a file's edits from the status the clean check reads, so deleting the
    // directory would take them. `ls-files -v` tags the first `S`/`s` and the second in lower case (as in
    // ArchitectWorktree.HiddenAsync); only a file on disk counts, so the absent ones of a sparse checkout pass.
    private static async Task<string?> HiddenEditsAsync(string path, CancellationToken cancellationToken)
    {
        (int exitCode, string output, string stderr) = await FreeGitAsync(path, ["ls-files", "-v", "-z"], cancellationToken);
        if (exitCode != 0)
            return Reason("ls-files", exitCode, stderr, "");

        string[] hidden =
        [
            .. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => entry.Length > 2 && (entry[0] == 'S' || char.IsAsciiLetterLower(entry[0])))
                .Select(entry => entry[2..])
                .Where(file => Path.GetFullPath(file, path) is var full && (Path.Exists(full) || new FileInfo(full).LinkTarget is not null)),
        ];

        return hidden.Length == 0
            ? null
            : $"{hidden[0]}{(hidden.Length > 1 ? $" (and {hidden.Length - 1} more)" : "")} is marked skip-worktree or assume-unchanged, which hides its edits from git — "
                + "clear the mark (`git update-index --no-skip-worktree` or `--no-assume-unchanged`) and look at it first";
    }

    // On Unix git shares Claustrum's process group, so the terminal's SIGINT kills it before the token is cancelled
    // (measured 2026-10-13): .NET reports 128 + the signal. Every git call of a free goes through here so that a
    // Ctrl-C reads as an interrupted free (a `cancelled` receipt), never as git saying no. 131 is SIGQUIT, 129 SIGHUP
    // (#111): CancelSignalHandler treats both as a cancel too. The `--branch` door's reads before the free go through here as well.
    private static async Task<(int ExitCode, string Stdout, string Stderr)> FreeGitAsync(
        string cwd, string[] args, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        (int exitCode, string stdout, string stderr) = timeout is { } bound
            ? await GitProcess.RunAsync(cwd, args, bound, cancellationToken)
            : await GitProcess.RunAsync(cwd, args, cancellationToken);
        if (!OperatingSystem.IsWindows() && exitCode is SigHupExit or SigIntExit or SigQuitExit or SigTermExit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new WorktreeInterruptedException($"git {args.First(arg => !arg.StartsWith('-'))} was interrupted (exit {exitCode}); nothing was lost");
        }

        return (exitCode, stdout, stderr);
    }

    private const int SigHupExit = 129;
    private const int SigIntExit = 130;
    private const int SigQuitExit = 131;
    private const int SigTermExit = 143;

    // #74: a `coordinate` architect's worktree holds its children's in its own .claustrum/worktrees,
    // which its status ignores and a plain `worktree remove` deletes anyway — a child's uncommitted file
    // included, exit 0, the child left registered `prunable` (measured 2026-10-09, git 2.56).
    // H1: only a directory with a `.git` of its own counts — the empty one a `160000` entry in HEAD checks
    // out in every worktree holds nothing (NOTES.md #74 "Review round 3").
    private static string? NestedWorktrees(string path)
    {
        string nested = Path.Combine(path, ".claustrum", "worktrees");
        return Directory.Exists(nested) && Directory.EnumerateDirectories(nested).Any(child => Path.Exists(Path.Combine(child, ".git")))
            ? $"it holds job worktrees of its own under {nested} — run `claustrum jobs clean --cwd \"{path}\"` first"
            : null;
    }

    // Asked only at the top of a working tree: anywhere else git reads the main checkout, whose changes
    // are not this path's — and `worktree remove` refuses such a path by itself ("not a working tree").
    private static async Task<string?> UncommittedAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return null;

        try
        {
            (int prefixExit, string prefix, _) = await FreeGitAsync(path, ["rev-parse", "--show-prefix"], cancellationToken);
            if (prefixExit != 0 || prefix.Trim().Length > 0)
                return null;

            (int statusExit, string porcelain, string statusError) = await FreeGitAsync(path, statusArgs, cancellationToken);
            if (statusExit != 0)
                return Reason("status", statusExit, statusError, "");

            string[] lines = porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length switch
            {
                0 => null,
                1 => $"uncommitted changes: {lines[0].TrimEnd('\r')}",
                _ => $"uncommitted changes: {lines[0].TrimEnd('\r')} (and {lines.Length - 1} more)",
            };
        }
        catch (Win32Exception)
        {
            // Gone between the check above and git's start: nothing left in it to lose.
            return null;
        }
    }

    // The holder of the branch, if any, decides (#63): Claustrum frees only its own finished worktrees.
    // Refusal null: the branch is free. Warning: the freed worktree's trash that would not delete.
    private static async Task<(string? Refusal, string? Warning)> FreeBranchAsync(string cwd, string branch, string jobsRoot, CancellationToken cancellationToken)
    {
        if (!await BranchExistsAsync(cwd, branch, cancellationToken))
            return (NoSuchBranch(cwd, branch), null);

        // T1: a free killed after its rename leaves the branch held by an entry whose directory is gone, so such a holder
        // is not refused as someone else's checkout. Only that entry is released, never a repo-wide prune (review 2026-10-14).
        WorktreeEntry? held = await FindHolderAsync(cwd, branch, cancellationToken);
        (bool released, string? releaseFailure) = held is null ? (false, null) : await ReleaseDeadEntryAsync(cwd, held.Path, directoryGone: true, cancellationToken);
        if (released)
            held = await FindHolderAsync(cwd, branch, cancellationToken);

        if (held is not { Path: { } holder })
            return (null, null);

        string holderJobId = Path.GetFileName(Path.TrimEndingDirectorySeparator(holder));
        string ours = PathFor(cwd, holderJobId);
        if (holderJobId.Length == 0 || !await IsSameWorktreeAsync(ours, holder, cancellationToken))
        {
            // A `.git` file gone from a directory that still exists is not released: its staged index and reflog are work (round 3).
            string? unreleasedWhy = releaseFailure ?? (Path.Exists(holder) && !Path.Exists(Path.Combine(holder, ".git")) ? "its .git file is gone but its directory is not, so its registration was kept" : null);
            string unreleased = unreleasedWhy is null ? "" : $" ({unreleasedWhy})";
            return ($"--branch {branch}: already checked out in {holder}{unreleased}; a branch can be checked out in one worktree at a time — switch that checkout off it, then retry", null);
        }

        // R3: freed only on a receipt this jobs root holds — not `jobs clean`'s wider rule, under which a
        // job of another CLAUSTRUM_HOME, or one keep_last pruned, looks finished while it may be running.
        if (!Directory.Exists(Path.Combine(jobsRoot, holderJobId)))
        {
            return ($"--branch {branch}: checked out in {holder}, the worktree of job {holderJobId}, which is unknown under this CLAUSTRUM_HOME "
                + $"({jobsRoot}) — if it is finished, run `claustrum jobs clean --cwd \"{cwd}\"` there first", null);
        }

        if (!JobDirectory.HasResult(jobsRoot, holderJobId))
            return ($"--branch {branch}: checked out in {holder}, the worktree of job {holderJobId}, which has not finished (no result.json)", null);

        // #74 round 5 J4: a populated submodule is refused before the clean check can say "dirty", and its remedy is
        // `--force` — for a clean worktree only: a dirty one gets the generic text, as TryFreeAsync's own check would.
        if (await HoldsPopulatedSubmoduleAsync(ours, cancellationToken))
        {
            return ((NestedWorktrees(ours) ?? await UncommittedAsync(ours, cancellationToken)) is { } dirty
                ? LeftInPlace(branch, holder, holderJobId, dirty)
                : PopulatedSubmodule(branch, ours), null);
        }

        // Not forced: a finished job whose commit failed (#61's warning) still has its work there uncommitted,
        // and taking its branch back must not destroy it.
        (string? refusal, string? warning) = await TryFreeAsync(cwd, ours, cancellationToken);
        return (refusal is null ? null : LeftInPlace(branch, holder, holderJobId, refusal), warning);
    }

    private static string LeftInPlace(string branch, string holder, string holderJobId, string refusal)
    {
        return $"--branch {branch}: checked out in {holder}, the worktree of finished job {holderJobId}, which was left in place "
            + $"({refusal}) — commit or discard its changes, then retry";
    }

    private static string PopulatedSubmodule(string branch, string ours)
    {
        return $"--branch {branch}: its previous worktree {ours} holds a populated submodule, which git will not remove — remove it by hand "
            + $"(`git worktree remove --force \"{ours}\"`, which also deletes any commit made inside the submodule and pushed nowhere) and retry";
    }

    // J4: git's own test (`validate_no_submodules`, git 2.56) — the worktree's own `modules` directory, where a submodule
    // initialised in it keeps its git directory, or a gitlink in its index whose directory holds a `.git`.
    private static async Task<bool> HoldsPopulatedSubmoduleAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            (int modulesExit, string modules, _) = await FreeGitAsync(worktreePath, ["rev-parse", "--git-path", "modules"], cancellationToken);
            if (modulesExit == 0 && Directory.Exists(Path.Combine(worktreePath, modules.TrimEnd('\r', '\n'))))
                return true;

            // `-s -z`: `<mode> <sha> <stage>\t<path>` per entry, the path raw.
            (int filesExit, string files, _) = await FreeGitAsync(worktreePath, ["ls-files", "-s", "-z"], cancellationToken);
            return filesExit == 0 && files.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => entry.StartsWith("160000 ", StringComparison.Ordinal))
                .Any(entry => Path.Exists(Path.Combine(worktreePath, entry[(entry.IndexOf('\t') + 1)..], ".git")));
        }
        catch (Win32Exception)
        {
            // Gone between the holder check and git's start: TryFreeAsync answers for it.
            return false;
        }
    }

    private static async Task<WorktreeEntry?> FindHolderAsync(string cwd, string branch, CancellationToken cancellationToken) =>
        (await ListWorktreesAsync(cwd, cancellationToken))?.FirstOrDefault(entry => entry.Branch == $"refs/heads/{branch}");

    // `git worktree list --porcelain`: a `worktree <path>` line opens each entry (the main checkout first); the entry
    // with a branch checked out carries `branch refs/heads/<name>`, a locked one a `locked` line. Line mode, not -z: -z
    // needs git 2.36. Null when git fails.
    private static async Task<WorktreeEntry[]?> ListWorktreesAsync(string cwd, CancellationToken cancellationToken)
    {
        (int exitCode, string stdout, _) = await FreeGitAsync(cwd, ["worktree", "list", "--porcelain"], cancellationToken);
        if (exitCode != 0)
            return null;

        List<WorktreeEntry> entries = [];
        foreach (string line in stdout.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                entries.Add(new WorktreeEntry(line["worktree ".Length..], Branch: null, Locked: false));
            else if (entries.Count > 0 && line.StartsWith("branch ", StringComparison.Ordinal))
                entries[^1] = entries[^1] with { Branch = line["branch ".Length..] };
            else if (entries.Count > 0 && (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal)))
                entries[^1] = entries[^1] with { Locked = true };
        }

        return [.. entries];
    }

    private sealed record WorktreeEntry(string Path, string? Branch, bool Locked);

    // git lists a worktree by its realpath (measured 2026-10-08: added through a symlinked cwd, it is
    // listed under the link's target), so `ours` is compared through git's own answer, never as typed.
    private static async Task<bool> IsSameWorktreeAsync(string ours, string gitListed, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ours))
            return false;

        try
        {
            (int exitCode, string stdout, _) = await FreeGitAsync(ours, ["rev-parse", "--show-toplevel"], cancellationToken);
            return exitCode == 0 && SamePath(stdout.Trim(), gitListed);
        }
        catch (Win32Exception)
        {
            // R1: removed between the check above and git's start (a `jobs clean` beside this run) — not ours to free.
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsGitFailure(Exception ex) => ex is TimeoutException or IOException or InvalidOperationException or Win32Exception;

    private static string Reason(string verb, int exitCode, string stderr, string stdout) =>
        FirstLine(stderr) ?? FirstLine(stdout) ?? $"git {verb} exited {exitCode}";

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    // StageAsync's answer: whether the index holds anything to commit, `add`'s stderr for the log, and the status
    // after it all — whatever is still unstaged there is what the commit leaves out.
    private sealed record Staging(bool AnyStaged, string AddError, StatusEntry[] Remainder)
    {
        public static readonly Staging None = new(AnyStaged: false, AddError: "", Remainder: []);
    }

    // One `status --porcelain=v2 -z` entry: `1 XY sub mH mI mW hH hI path`, `2` the same with a score before the
    // path and the old path in the next field, `u XY sub m1 m2 m3 mW h1 h2 h3 path`, `? path`. Unstaged: a worktree
    // side (Y not `.`, or untracked); Submodule: the `S…` field. A `# …` header (round 5, J3) is no path; any other
    // shape this does not know fails closed: unstaged.
    private sealed record StatusEntry(string Path, bool Unstaged, bool Submodule)
    {
        public static StatusEntry[] Parse(string output)
        {
            List<StatusEntry> entries = [];
            string[] fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < fields.Length; index++)
            {
                string field = fields[index];
                if (field[0] == '#')
                    continue;

                int pathAt = field[0] switch { '1' => 8, '2' => 9, 'u' => 10, '?' => 1, _ => -1 };
                string[] parts = pathAt < 0 ? [] : field.Split(' ', pathAt + 1);
                if (pathAt < 0 || parts.Length <= pathAt)
                {
                    entries.Add(new StatusEntry(field, Unstaged: true, Submodule: false));
                    continue;
                }

                // `?` lists an embedded repository as `dir/`; the warnings name it as the gitlink would be.
                bool untracked = pathAt == 1;
                entries.Add(new StatusEntry(parts[pathAt].TrimEnd('/'), Unstaged: untracked || parts[1] is not [_, '.'], Submodule: !untracked && parts[2].StartsWith('S')));
                if (field[0] == '2')
                    index++;
            }

            return [.. entries];
        }
    }
}

// MainCheckout is the request cwd the worktree was cut from — the checkout a delegate is told never to
// touch (#62). BaseCommit is the branch's tip when the worktree was made: what the run started from
// (F4, F9). OwnsBranch is false for a `--branch` run, whose branch must outlive any cleanup (#63).
// FreeWarning: what freeing the branch's previous worktree left behind (#106), for the run's receipt.
public sealed record JobWorktreeInfo(string Path, string Branch, string MainCheckout, string BaseCommit, bool OwnsBranch, string? FreeWarning = null);
