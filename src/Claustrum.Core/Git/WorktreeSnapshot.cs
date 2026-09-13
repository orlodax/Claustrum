using System.Diagnostics;
using System.Text;
using Claustrum.Core.Model;

namespace Claustrum.Core.Git;

// Harness-neutral changed-files capture (docs/PLAN.md A2): `git status --porcelain=v1 -z
// --untracked-files=all` + `git diff HEAD` before and after the backend runs; changed_files is the
// set difference between the two status snapshots, so a file already dirty before the run (and
// left exactly as dirty) is not reported. Non-git cwd falls back to an mtime/size scan; diff is
// then always null. Renames/copies are folded into Added — see NOTES.md "Worktree snapshot".
public static class WorktreeSnapshot
{
    public static async Task<WorktreeState> CaptureAsync(string cwd, CancellationToken cancellationToken = default)
    {
        (int exitCode, string stdout, _) = await RunGitAsync(cwd, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        return exitCode == 0
            ? new WorktreeState(IsGit: true, ParseStatusZ(stdout), FileScan: null)
            : new WorktreeState(IsGit: false, StatusEntries: [], FileScan: ScanFiles(cwd));
    }

    public static async Task<SnapshotDiff> DiffAsync(string cwd, WorktreeState before, WorktreeState after, int diffByteCapBytes, CancellationToken cancellationToken = default)
    {
        if (!before.IsGit || !after.IsGit)
            return DiffFileScans(before.FileScan!, after.FileScan!);

        Dictionary<string, GitStatusEntry> beforeByPath = before.StatusEntries.ToDictionary(entry => entry.Path);
        List<GitStatusEntry> changedEntries = [.. after.StatusEntries.Where(entry =>
            !beforeByPath.TryGetValue(entry.Path, out GitStatusEntry? prior)
            || prior.IndexStatus != entry.IndexStatus
            || prior.WorktreeStatus != entry.WorktreeStatus)];

        if (changedEntries.Count == 0)
            return new SnapshotDiff([], null, false);

        ChangedFile[] changedFiles = [.. changedEntries.Select(ToChangedFile)];
        string fullDiff = await BuildDiffAsync(cwd, changedEntries, cancellationToken);

        byte[] bytes = Encoding.UTF8.GetBytes(fullDiff);
        bool truncated = bytes.Length > diffByteCapBytes;
        string diff = truncated ? Encoding.UTF8.GetString(bytes, 0, diffByteCapBytes) : fullDiff;
        return new SnapshotDiff(changedFiles, diff, truncated);
    }

    private static async Task<string> BuildDiffAsync(string cwd, List<GitStatusEntry> changedEntries, CancellationToken cancellationToken)
    {
        string[] trackedPaths = [.. changedEntries.Where(entry => !IsUntracked(entry)).Select(entry => entry.Path)];
        string[] untrackedPaths = [.. changedEntries.Where(IsUntracked).Select(entry => entry.Path)];

        StringBuilder diffBuilder = new();
        if (trackedPaths.Length > 0)
        {
            (_, string trackedDiff, _) = await RunGitAsync(cwd, ["diff", "HEAD", "--", .. trackedPaths], cancellationToken);
            diffBuilder.Append(trackedDiff);
        }

        // Untracked files have no HEAD blob, so `git diff HEAD` silently ignores them; git treats
        // "/dev/null" as a diff pseudo-path on every OS, including Windows.
        foreach (string path in untrackedPaths)
        {
            (int exitCode, string stdout, _) = await RunGitAsync(cwd, ["diff", "--no-index", "--", "/dev/null", path], cancellationToken);
            if (exitCode is 0 or 1)
                diffBuilder.Append(stdout);
        }

        return diffBuilder.ToString();
    }

    private static GitStatusEntry[] ParseStatusZ(string raw)
    {
        string[] tokens = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<GitStatusEntry> entries = [];
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token.Length < 3)
                continue;

            char indexStatus = token[0];
            char worktreeStatus = token[1];
            string path = token[3..];
            string? oldPath = indexStatus is 'R' or 'C' && i + 1 < tokens.Length ? tokens[++i] : null;

            entries.Add(new GitStatusEntry(path, indexStatus, worktreeStatus, oldPath));
        }

        return [.. entries];
    }

    private static ChangedFile ToChangedFile(GitStatusEntry entry) => new(entry.Path, ClassifyKind(entry));

    private static ChangeKind ClassifyKind(GitStatusEntry entry)
    {
        if (entry.IndexStatus == 'D' || entry.WorktreeStatus == 'D')
            return ChangeKind.Deleted;
        if (IsUntracked(entry) || entry.IndexStatus == 'A')
            return ChangeKind.Added;
        return ChangeKind.Modified;
    }

    private static bool IsUntracked(GitStatusEntry entry) => entry.IndexStatus == '?' && entry.WorktreeStatus == '?';

    private static Dictionary<string, (long Size, DateTime ModifiedUtc)> ScanFiles(string cwd)
    {
        Dictionary<string, (long, DateTime)> result = [];
        foreach (string path in Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories))
        {
            FileInfo info = new(path);
            result[Path.GetRelativePath(cwd, path)] = (info.Length, info.LastWriteTimeUtc);
        }

        return result;
    }

    private static SnapshotDiff DiffFileScans(Dictionary<string, (long Size, DateTime ModifiedUtc)> before, Dictionary<string, (long Size, DateTime ModifiedUtc)> after)
    {
        List<ChangedFile> changed = [];
        foreach (KeyValuePair<string, (long Size, DateTime ModifiedUtc)> entry in after)
        {
            if (!before.TryGetValue(entry.Key, out (long Size, DateTime ModifiedUtc) prior))
                changed.Add(new ChangedFile(entry.Key, ChangeKind.Added));
            else if (prior != entry.Value)
                changed.Add(new ChangedFile(entry.Key, ChangeKind.Modified));
        }

        foreach (string path in before.Keys.Except(after.Keys))
            changed.Add(new ChangedFile(path, ChangeKind.Deleted));

        return new SnapshotDiff([.. changed], null, false);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(string cwd, string[] args, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using System.Diagnostics.Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
