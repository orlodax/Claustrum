using System.Security.Cryptography;
using System.Text;
using Claustrum.Core.Model;

namespace Claustrum.Core.Git;

// Harness-neutral changed-files capture (docs/PLAN.md A2): `git status --porcelain=v1 -z
// --untracked-files=all` + `git diff HEAD` before and after the backend runs. A file is reported
// when its status code changes OR its content changes — each status entry carries a hash of the
// worktree file's current bytes, so a file already dirty before the run that gets edited again
// during it is still caught (NOTES.md "Worktree snapshot"). Non-git cwd falls back to an mtime/size
// scan; diff is then always null. A rename (`R` in either the index or worktree column) is split
// into two ChangedFiles: the new path as `A`, the old path as `D`.
public static class WorktreeSnapshot
{
    public static async Task<WorktreeState> CaptureAsync(string cwd, CancellationToken cancellationToken = default)
    {
        (int exitCode, string stdout, _) = await GitProcess.RunAsync(cwd, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (exitCode != 0)
            return new WorktreeState.FileScan(ScanFiles(cwd));

        GitStatusEntry[] entries = [.. ParseStatusZ(stdout).Select(entry => entry with { ContentHash = HashWorktreeFile(cwd, entry) })];
        return new WorktreeState.Git(entries);
    }

    public static async Task<SnapshotDiff> DiffAsync(string cwd, WorktreeState before, WorktreeState after, int diffByteCapBytes, CancellationToken cancellationToken = default)
    {
        if (before is WorktreeState.Git beforeGit && after is WorktreeState.Git afterGit)
            return await DiffGitAsync(cwd, beforeGit.StatusEntries, afterGit.StatusEntries, diffByteCapBytes, cancellationToken);

        if (before is WorktreeState.FileScan beforeScan && after is WorktreeState.FileScan afterScan)
            return DiffFileScans(beforeScan.Files, afterScan.Files);

        throw new InvalidOperationException("before/after worktree snapshots must both be git or both be non-git captures of the same cwd");
    }

    private static async Task<SnapshotDiff> DiffGitAsync(string cwd, GitStatusEntry[] beforeEntries, GitStatusEntry[] afterEntries, int diffByteCapBytes, CancellationToken cancellationToken)
    {
        Dictionary<string, GitStatusEntry> beforeByPath = beforeEntries.ToDictionary(entry => entry.Path);
        List<GitStatusEntry> changedEntries = [.. afterEntries.Where(entry =>
            !beforeByPath.TryGetValue(entry.Path, out GitStatusEntry? prior)
            || prior.IndexStatus != entry.IndexStatus
            || prior.WorktreeStatus != entry.WorktreeStatus
            || prior.ContentHash != entry.ContentHash)];

        if (changedEntries.Count == 0)
            return new SnapshotDiff([], null, false);

        ChangedFile[] changedFiles = [.. changedEntries.SelectMany(ToChangedFiles)];
        string fullDiff = await BuildDiffAsync(cwd, changedEntries, cancellationToken);
        return Capped(changedFiles, fullDiff, diffByteCapBytes);
    }

    /// <summary>
    /// F4: an isolated run's changes once its branch moved past <paramref name="baseCommit"/>, where
    /// the run started — the working tree against that commit, so what the runner or the role committed
    /// shows beside what is still uncommitted; untracked new files come from <paramref name="snapshot"/>,
    /// as in <see cref="DiffAsync"/>. Run only in a verified job worktree. Null when git cannot say.
    /// NOTES.md "An isolated run's receipt is the branch's delta from its starting commit".
    /// </summary>
    public static async Task<SnapshotDiff?> BranchDeltaAsync(string worktree, string baseCommit, SnapshotDiff snapshot, int diffByteCapBytes, CancellationToken cancellationToken = default)
    {
        // --no-renames: git itself reports a rename as the A + D pair the snapshot splits R into.
        (int namesExit, string names, _) = await GitProcess.RunAsync(worktree, ["diff", "--name-status", "--no-renames", "-z", baseCommit, "--"], cancellationToken);
        (int diffExit, string trackedDiff, _) = await GitProcess.RunAsync(worktree, ["diff", baseCommit, "--"], cancellationToken);
        (int untrackedExit, string untrackedNow, _) = await GitProcess.RunAsync(worktree, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        if (namesExit != 0 || diffExit != 0 || untrackedExit != 0)
            return null;

        // `git diff <commit>` sees the index only: a new file the runner's commit could not take is still
        // untracked, and the snapshot is what knows the run made it.
        HashSet<string> untrackedPaths = [.. untrackedNow.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        string[] untracked = [.. snapshot.ChangedFiles
            .Where(file => file.Kind == ChangeKind.Added && untrackedPaths.Contains(file.Path))
            .Select(file => file.Path)];

        ChangedFile[] changedFiles = [.. ParseNameStatusZ(names), .. untracked.Select(path => new ChangedFile(path, ChangeKind.Added))];
        if (changedFiles.Length == 0)
            return new SnapshotDiff([], null, false);

        StringBuilder diffBuilder = new(trackedDiff);
        await AppendUntrackedDiffsAsync(worktree, untracked, diffBuilder, cancellationToken);
        return Capped(changedFiles, diffBuilder.ToString(), diffByteCapBytes);
    }

    private static async Task<string> BuildDiffAsync(string cwd, List<GitStatusEntry> changedEntries, CancellationToken cancellationToken)
    {
        string[] trackedPaths = [.. changedEntries.Where(entry => !IsUntracked(entry)).Select(entry => entry.Path)];
        string[] untrackedPaths = [.. changedEntries.Where(IsUntracked).Select(entry => entry.Path)];

        StringBuilder diffBuilder = new();
        if (trackedPaths.Length > 0)
        {
            (_, string trackedDiff, _) = await GitProcess.RunAsync(cwd, ["diff", "HEAD", "--", .. trackedPaths], cancellationToken);
            diffBuilder.Append(trackedDiff);
        }

        await AppendUntrackedDiffsAsync(cwd, untrackedPaths, diffBuilder, cancellationToken);
        return diffBuilder.ToString();
    }

    // Untracked files have no HEAD blob, so `git diff HEAD` silently ignores them; git treats
    // "/dev/null" as a diff pseudo-path on every OS, including Windows.
    private static async Task AppendUntrackedDiffsAsync(string cwd, IEnumerable<string> untrackedPaths, StringBuilder diffBuilder, CancellationToken cancellationToken)
    {
        foreach (string path in untrackedPaths)
        {
            (int exitCode, string stdout, _) = await GitProcess.RunAsync(cwd, ["diff", "--no-index", "--", "/dev/null", path], cancellationToken);
            if (exitCode is 0 or 1)
                diffBuilder.Append(stdout);
        }
    }

    private static SnapshotDiff Capped(ChangedFile[] changedFiles, string fullDiff, int diffByteCapBytes)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(fullDiff);
        bool truncated = bytes.Length > diffByteCapBytes;
        string diff = truncated ? TruncateUtf8(bytes, diffByteCapBytes) : fullDiff;
        return new SnapshotDiff(changedFiles, diff, truncated);
    }

    // `git diff --name-status --no-renames -z`: a status letter, then its path, NUL-separated. T (a type
    // change) and anything rarer count as modified.
    private static IEnumerable<ChangedFile> ParseNameStatusZ(string raw)
    {
        string[] tokens = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            ChangeKind kind = tokens[i][0] switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                _ => ChangeKind.Modified,
            };
            yield return new ChangedFile(tokens[i + 1], kind);
        }
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
            bool isRenameOrCopy = indexStatus is 'R' or 'C' || worktreeStatus == 'R';
            string? oldPath = isRenameOrCopy && i + 1 < tokens.Length ? tokens[++i] : null;

            entries.Add(new GitStatusEntry(path, indexStatus, worktreeStatus, oldPath, ContentHash: null));
        }

        return [.. entries];
    }

    // A rename (`R` in either column) is reported as two ChangedFiles — the new path as Added, the
    // old path as Deleted — instead of folding into a single Added, which used to silently drop the
    // old path from `changed_files` (NOTES.md "Worktree snapshot"). A copy (`C`, index column only)
    // has no old-path removal: the source is untouched, only the new path is Added.
    private static IEnumerable<ChangedFile> ToChangedFiles(GitStatusEntry entry)
    {
        if (entry.OldPath is { } oldPath && (entry.IndexStatus == 'R' || entry.WorktreeStatus == 'R'))
        {
            yield return new ChangedFile(entry.Path, ChangeKind.Added);
            yield return new ChangedFile(oldPath, ChangeKind.Deleted);
            yield break;
        }

        yield return new ChangedFile(entry.Path, ClassifyKind(entry));
    }

    private static ChangeKind ClassifyKind(GitStatusEntry entry)
    {
        if (entry.IndexStatus == 'D' || entry.WorktreeStatus == 'D')
            return ChangeKind.Deleted;
        if (IsUntracked(entry) || entry.IndexStatus is 'A' or 'C')
            return ChangeKind.Added;
        return ChangeKind.Modified;
    }

    private static bool IsUntracked(GitStatusEntry entry) => entry.IndexStatus == '?' && entry.WorktreeStatus == '?';

    private static string? HashWorktreeFile(string cwd, GitStatusEntry entry)
    {
        string fullPath = Path.Combine(cwd, entry.Path);
        if (!File.Exists(fullPath))
            return null;

        try
        {
            using FileStream stream = File.OpenRead(fullPath);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 2026-09-14 (issue #3): a file locked FileShare.None by an editor/AV/OneDrive sync, or
            // one that disappears between File.Exists and File.OpenRead, is unhashable, not fatal —
            // this used to throw out of Runner entirely (NOTES.md "Worktree snapshot: an unreadable
            // file is unhashable, not fatal"). Consequence: if the same file stays locked across both
            // snapshots, a content-only edit to it can be missed in changed_files.
            return null;
        }
    }

    // Backs up over UTF-8 continuation bytes (`10xxxxxx`) so a multi-byte codepoint straddling the
    // cap is dropped whole rather than decoded into a replacement character.
    private static string TruncateUtf8(byte[] bytes, int capBytes)
    {
        if (capBytes >= bytes.Length)
            return Encoding.UTF8.GetString(bytes);

        int end = capBytes;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
            end--;

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    // Walked by hand rather than with SearchOption.AllDirectories because 2026-09-21 (issue #12) one
    // unreadable subdirectory in a probe's cwd threw UnauthorizedAccessException out of the *after*
    // snapshot and turned a finished run into Failed. Same convention as HashWorktreeFile: an entry
    // that cannot be read is skipped, so a change under it is missed rather than fatal.
    private static Dictionary<string, (long Size, DateTime ModifiedUtc)> ScanFiles(string cwd)
    {
        Dictionary<string, (long, DateTime)> result = [];
        Stack<string> pending = new();
        pending.Push(cwd);

        while (pending.TryPop(out string? directory))
        {
            foreach (string subdirectory in ReadableEntries(() => Directory.GetDirectories(directory)))
                pending.Push(subdirectory);

            foreach (string path in ReadableEntries(() => Directory.GetFiles(directory)))
            {
                try
                {
                    FileInfo info = new(path);
                    result[Path.GetRelativePath(cwd, path)] = (info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished mid-scan, or whose metadata is unreadable, contributes no entry.
                }
            }
        }

        return result;
    }

    // Eager Get* rather than Enumerate*: a lazy enumerator throws on MoveNext, which a try around the
    // call site would not catch.
    private static string[] ReadableEntries(Func<string[]> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
}
