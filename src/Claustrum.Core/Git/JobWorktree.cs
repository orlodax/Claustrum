using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
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

    // R2: every status here lists new files whatever `status.showUntrackedFiles` says (repo or global):
    // under `no`, a plain `--porcelain` hid a role's new files from the commit and from the remove check.
    private static readonly string[] statusArgs = ["status", "--porcelain", "--untracked-files=all"];

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
        (int exitCode, _, _) = await GitProcess.RunAsync(cwd, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
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
    /// with no worktree left (F11, T1); a cancel still throws, once the add is undone. The caller holds
    /// the branch's lock (<see cref="LockKeyFor"/>, R1) around the whole run.
    /// </summary>
    public static async Task<JobWorktreeInfo> CheckOutBranchAsync(string cwd, string jobId, string branch, string jobsRoot, CancellationToken cancellationToken)
    {
        string tip;
        try
        {
            if (await FreeBranchAsync(cwd, branch, jobsRoot, cancellationToken) is { } refusal)
                throw new BranchRefusedException(refusal);

            // The full ref, which show-ref just verified, read before the add: nothing holds the branch now.
            (int tipExit, string tipOutput, _) = await GitProcess.RunAsync(cwd, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken);
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
        JobWorktreeInfo worktree = new(PathFor(cwd, jobId), branch, cwd, tip, OwnsBranch: false);
        return await TryAddAsync(cwd, worktree, [branch], cancellationToken) is { } failure
            ? throw new BranchRefusedException($"--branch {branch}: {failure}")
            : worktree;
    }

    /// <summary>
    /// `jobs clean`'s removal of a finished job's worktree. Never `--force` (F3): since #61 a finished
    /// worktree is clean, and one that is not — a commit git refused, a hard-killed run, a submodule the
    /// role populated, a new file `status.showUntrackedFiles=no` hides (R2) — is work nobody has looked
    /// at, so it throws naming the path and stays. The branch is untouched either way, so the architect
    /// can still rebase from it.
    /// </summary>
    public static async Task RemoveAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        string path = PathFor(cwd, jobId);
        if (await TryRemoveAsync(cwd, path, force: false, cancellationToken) is { } refusal)
            throw new InvalidOperationException($"{path} left in place: {refusal}");
    }

    /// <summary>
    /// Undoes a successful <see cref="AddAsync"/>/<see cref="CheckOutBranchAsync"/> for a job that
    /// produced no result (a failed add undoes itself, T1). `--force`, and `branch -D` of a branch the
    /// run created (<c>-b</c>; a `--branch` run's outlives it, #63), only when nothing of the run's can
    /// be in there: on its branch, the branch still at <see cref="JobWorktreeInfo.BaseCommit"/>,
    /// nothing uncommitted. Otherwise — a run whose result.json could not be written after the runner
    /// committed — the branch is kept and the remove is plain, so a dirty worktree stays (F9).
    /// Best-effort: this runs on a failure path whose own exception is the one worth surfacing.
    /// </summary>
    /// <returns>Whatever went wrong while cleaning up, or null on success.</returns>
    public static async Task<Exception?> TryRemoveAbandonedAsync(string cwd, JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsUntouchedAsync(worktree, cancellationToken))
            {
                return await TryRemoveAsync(cwd, worktree.Path, force: false, cancellationToken) is { } refusal
                    ? new InvalidOperationException($"{worktree.Path} left in place, branch {worktree.Branch} kept: {refusal}")
                    : null;
            }

            if (await TryRemoveAsync(cwd, worktree.Path, force: true, cancellationToken) is { } failure)
                return new InvalidOperationException($"git worktree remove {worktree.Path} failed: {failure}");

            return await TryDeleteOwnedBranchAsync(cwd, worktree, cancellationToken) is { } branchFailure
                ? new InvalidOperationException($"git branch -D {worktree.Branch} failed: {branchFailure}")
                : null;
        }
        catch (Exception ex) when (IsGitFailure(ex))
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
    /// </summary>
    public static async Task<BranchReceipt> CommitRunAsync(JobWorktreeInfo worktree, string message, SnapshotDiff snapshot, int diffByteCapBytes, CancellationToken cancellationToken)
    {
        if (await VerifyAsync(worktree, cancellationToken) is { } mismatch)
            return Unverified(worktree, mismatch, snapshot);

        string? failure;
        try
        {
            failure = await CommitAllAsync(worktree.Path, message, cancellationToken);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            failure = ex.Message;
        }

        string[] warnings = failure is null ? [] : [$"work left uncommitted on {worktree.Branch}: {failure}"];
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

    /// <summary>
    /// The receipt of a run whose worktree <see cref="VerifyAsync"/> rejected: nothing committed, no
    /// delta, no `commit`. R5: a path that is no longer the job's worktree reports no changes either —
    /// git would have read them from the main checkout — and a branch whose worktree went away mid-run
    /// may since be another run's (R3), so its tip says nothing about this one. T3: the job's own
    /// worktree, only off its branch, keeps <paramref name="snapshot"/>, which was read inside it.
    /// </summary>
    public static BranchReceipt Unverified(JobWorktreeInfo worktree, WorktreeMismatch mismatch, SnapshotDiff snapshot)
    {
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
    /// <see cref="WorktreeMismatch.OwnWorktree"/>: safe to read, never to commit.
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

            return head.Trim() == $"refs/heads/{worktree.Branch}" ? null : new WorktreeMismatch(head.Trim(), OwnWorktree: true);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            // Removed between the check above and git's start (Win32Exception), or a git past its bound.
            return new WorktreeMismatch(ex.Message, OwnWorktree: false);
        }
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

    // A git that cannot start or passes its bound throws, and CommitRunAsync reads that as "nothing
    // committed" — NOTES.md "A commit that was made is never reported as left uncommitted" has the limits.
    private static async Task<string?> CommitAllAsync(string path, string message, CancellationToken cancellationToken)
    {
        (int statusExit, string porcelain, string statusError) = await GitProcess.RunAsync(path, statusArgs, cancellationToken);
        if (statusExit != 0)
            return Reason("status", statusExit, statusError, "");
        if (porcelain.Trim().Length == 0)
            return null;

        (int addExit, string addOutput, string addError) = await GitProcess.RunAsync(path, ["add", "-A"], repoCodeTimeout, cancellationToken);
        if (addExit != 0)
            return Reason("add", addExit, addError, addOutput);

        (int commitExit, string commitOutput, string commitError) =
            await GitProcess.RunAsync(path, ["commit", "-q", "-m", message], repoCodeTimeout, cancellationToken);
        return commitExit == 0 ? null : Reason("commit", commitExit, commitError, commitOutput);
    }

    // F9: provably nothing of the run's in it — so `--force` and deleting the branch lose nothing.
    private static async Task<bool> IsUntouchedAsync(JobWorktreeInfo worktree, CancellationToken cancellationToken)
    {
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
            // Still a cancel, so `run` exits 130; what the undo could not remove rides on the message,
            // which RunCommand.Cancelled prints.
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

    // T1: the role never ran, so nothing in the worktree is anyone's. `-f -f`, because git killed
    // mid-checkout leaves the entry `locked initializing`, which `remove` and `remove --force` refuse.
    // The repo-wide prune only after a failed remove: an entry whose `.git` file is gone fails it and
    // stays registered (both measured, git 2.56). The bound is the checkout's: deleting a large tree
    // is as slow as writing it. Null when nothing is left.
    private static async Task<string?> UndoAddAsync(string cwd, JobWorktreeInfo worktree)
    {
        try
        {
            (int exitCode, string stdout, string stderr) =
                await GitProcess.RunAsync(cwd, ["worktree", "remove", "--force", "--force", worktree.Path], repoCodeTimeout, CancellationToken.None);
            if (exitCode != 0)
                await GitProcess.RunAsync(cwd, ["worktree", "prune"], CancellationToken.None);

            List<string> left = [];
            if (Directory.Exists(worktree.Path))
                left.Add($"{worktree.Path} ({Reason("worktree remove", exitCode, stderr, stdout)})");
            if (await TryDeleteOwnedBranchAsync(cwd, worktree, CancellationToken.None) is { } branchFailure)
                left.Add($"branch {worktree.Branch} ({branchFailure})");

            return left.Count == 0 ? null : string.Join(" and ", left);
        }
        catch (Exception ex) when (IsGitFailure(ex))
        {
            string branch = worktree.OwnsBranch ? $" and branch {worktree.Branch}" : "";
            return $"possibly {worktree.Path}{branch} (undoing stopped: {ex.Message})";
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

    // Null when git removed it; otherwise why not. A plain remove asks git what is uncommitted first
    // (R2): git's own check honours `status.showUntrackedFiles=no` and then deletes new files silently.
    private static async Task<string?> TryRemoveAsync(string cwd, string path, bool force, CancellationToken cancellationToken)
    {
        if (!force && await UncommittedAsync(path, cancellationToken) is { } uncommitted)
            return uncommitted;

        string[] args = force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path];
        (int exitCode, string stdout, string stderr) = await GitProcess.RunAsync(cwd, args, cancellationToken);
        return exitCode == 0 ? null : Reason("worktree remove", exitCode, stderr, stdout);
    }

    // Asked only at the top of a working tree: anywhere else git reads the main checkout, whose changes
    // are not this path's — and `worktree remove` refuses such a path by itself ("not a working tree").
    private static async Task<string?> UncommittedAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return null;

        try
        {
            (int prefixExit, string prefix, _) = await GitProcess.RunAsync(path, ["rev-parse", "--show-prefix"], cancellationToken);
            if (prefixExit != 0 || prefix.Trim().Length > 0)
                return null;

            (int statusExit, string porcelain, string statusError) = await GitProcess.RunAsync(path, statusArgs, cancellationToken);
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
    private static async Task<string?> FreeBranchAsync(string cwd, string branch, string jobsRoot, CancellationToken cancellationToken)
    {
        if (!await BranchExistsAsync(cwd, branch, cancellationToken))
            return NoSuchBranch(cwd, branch);

        // T1: a free killed mid-remove can leave the branch held by an entry whose directory, or its `.git`,
        // is gone. prune drops exactly those — not an intact worktree, not a locked one (measured, git
        // 2.56) — so such a holder is not refused as someone else's checkout.
        await GitProcess.RunAsync(cwd, ["worktree", "prune"], cancellationToken);

        if (await FindHolderAsync(cwd, branch, cancellationToken) is not { } holder)
            return null;

        string holderJobId = Path.GetFileName(Path.TrimEndingDirectorySeparator(holder));
        string ours = PathFor(cwd, holderJobId);
        if (holderJobId.Length == 0 || !await IsSameWorktreeAsync(ours, holder, cancellationToken))
            return $"--branch {branch}: already checked out in {holder}; a branch can be checked out in one worktree at a time — switch that checkout off it, then retry";

        // R3: freed only on a receipt this jobs root holds — not `jobs clean`'s wider rule, under which a
        // job of another CLAUSTRUM_HOME, or one keep_last pruned, looks finished while it may be running.
        if (!Directory.Exists(Path.Combine(jobsRoot, holderJobId)))
        {
            return $"--branch {branch}: checked out in {holder}, the worktree of job {holderJobId}, which is unknown under this CLAUSTRUM_HOME "
                + $"({jobsRoot}) — if it is finished, run `claustrum jobs clean --cwd \"{cwd}\"` there first";
        }

        if (!JobDirectory.HasResult(jobsRoot, holderJobId))
            return $"--branch {branch}: checked out in {holder}, the worktree of job {holderJobId}, which has not finished (no result.json)";

        // Not --force: a finished job whose commit failed (#61's warning) still has its work there
        // uncommitted, and taking its branch back must not destroy it.
        return await TryRemoveAsync(cwd, ours, force: false, cancellationToken) is { } refusal
            ? $"--branch {branch}: checked out in {holder}, the worktree of finished job {holderJobId}, which was left in place "
                + $"({refusal}) — commit or discard its changes, then retry"
            : null;
    }

    // `git worktree list --porcelain`: a `worktree <path>` line opens each entry, and the entry that has
    // the branch checked out carries `branch refs/heads/<name>`. Line mode, not -z: -z needs git 2.36.
    private static async Task<string?> FindHolderAsync(string cwd, string branch, CancellationToken cancellationToken)
    {
        (int exitCode, string stdout, _) = await GitProcess.RunAsync(cwd, ["worktree", "list", "--porcelain"], cancellationToken);
        if (exitCode != 0)
            return null;

        string? current = null;
        foreach (string line in stdout.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                current = line["worktree ".Length..];
            else if (line == $"branch refs/heads/{branch}")
                return current;
        }

        return null;
    }

    // git lists a worktree by its realpath (measured 2026-10-08: added through a symlinked cwd, it is
    // listed under the link's target), so `ours` is compared through git's own answer, never as typed.
    private static async Task<bool> IsSameWorktreeAsync(string ours, string gitListed, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ours))
            return false;

        try
        {
            (int exitCode, string stdout, _) = await GitProcess.RunAsync(ours, ["rev-parse", "--show-toplevel"], cancellationToken);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return exitCode == 0 && string.Equals(Normalize(stdout.Trim()), Normalize(gitListed), comparison);
        }
        catch (Win32Exception)
        {
            // R1: removed between the check above and git's start (a `jobs clean` beside this run) — not ours to free.
            return false;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsGitFailure(Exception ex) => ex is TimeoutException or IOException or InvalidOperationException or Win32Exception;

    private static string Reason(string verb, int exitCode, string stderr, string stdout) =>
        FirstLine(stderr) ?? FirstLine(stdout) ?? $"git {verb} exited {exitCode}";

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}

// MainCheckout is the request cwd the worktree was cut from — the checkout a delegate is told never to
// touch (#62). BaseCommit is the branch's tip when the worktree was made: what the run started from
// (F4, F9). OwnsBranch is false for a `--branch` run, whose branch must outlive any cleanup (#63).
public sealed record JobWorktreeInfo(string Path, string Branch, string MainCheckout, string BaseCommit, bool OwnsBranch);
