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
}

public sealed record JobWorktreeInfo(string Path, string Branch);
