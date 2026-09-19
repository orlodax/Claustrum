namespace Claustrum.Core.Git;

// `git worktree add`/`remove` isolation for a `max_parallel > 1` builder job (docs/PLAN.md §D4):
// each such job runs inside its own `.claustrum/worktrees/<job>` on branch `claustrum/<job>` so N
// concurrent builders never step on the same working tree, and the architect integrates by rebasing
// each branch onto its target rather than reading a shared, contended cwd.
public static class JobWorktree
{
    public static string PathFor(string cwd, string jobId) => Path.Combine(cwd, ".claustrum", "worktrees", jobId);

    public static string BranchFor(string jobId) => $"claustrum/{jobId}";

    public static async Task<JobWorktreeInfo> AddAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        string path = PathFor(cwd, jobId);
        string branch = BranchFor(jobId);
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "worktrees"));

        (int exitCode, _, string stderr) = await GitProcess.RunAsync(cwd, ["worktree", "add", path, "-b", branch], cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"git worktree add {path} -b {branch} failed: {stderr}");

        return new JobWorktreeInfo(path, branch);
    }

    // `--force` also removes a worktree with uncommitted changes: the job's own diff has already been
    // captured by WorktreeSnapshot before this runs, so nothing here needs preserving. The branch
    // itself is untouched — only the working directory goes away — so the architect can still rebase
    // from it afterwards (docs/PLAN.md §D4 "claustrum jobs clean removes worktrees of finished jobs").
    public static async Task RemoveAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        string path = PathFor(cwd, jobId);
        (int exitCode, _, string stderr) = await GitProcess.RunAsync(cwd, ["worktree", "remove", "--force", path], cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"git worktree remove {path} failed: {stderr}");
    }

    /// <summary>
    /// Undoes <see cref="AddAsync"/> for a job that never ran: removes the worktree *and* deletes the
    /// branch, unlike <see cref="RemoveAsync"/>, which keeps the branch because a finished job's
    /// commits live on it. Best-effort by design — this runs on a failure path whose own exception is
    /// the one worth surfacing, and `jobs clean` cannot pick the leftovers up later (it only removes
    /// worktrees whose job wrote a result.json, which a never-run job never does).
    /// </summary>
    /// <returns>Whatever went wrong while cleaning up, or null on success.</returns>
    public static async Task<Exception?> TryRemoveAbandonedAsync(string cwd, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await RemoveAsync(cwd, jobId, cancellationToken);

            // -D, not -d: the branch was cut from HEAD and never merged anywhere, so git's
            // merged-check would refuse the plain delete every time.
            (int exitCode, _, string stderr) = await GitProcess.RunAsync(cwd, ["branch", "-D", BranchFor(jobId)], cancellationToken);
            return exitCode == 0
                ? null
                : new InvalidOperationException($"git branch -D {BranchFor(jobId)} failed: {stderr}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return ex;
        }
    }
}

public sealed record JobWorktreeInfo(string Path, string Branch);
