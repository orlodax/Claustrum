using Claustrum.Core.Platform;

namespace Claustrum.Core.Git;

/// <summary>
/// Where a checkout's git metadata lives (#144), read from files, no git spawned. <see cref="TopLevel"/> holds the <c>.git</c> entry;
/// <see cref="CommonDir"/> is that <c>.git</c> directory, or for a linked worktree the <c>commondir</c> of the directory its <c>.git</c>
/// file names (<c>gitdir:</c>). <see cref="MainCheckout"/> is the common dir's parent when the common dir is called <c>.git</c>, else the
/// top level itself (a submodule, a separate git dir): a job worktree's own name is its job id, so projects match on this one.
/// </summary>
public sealed record GitCheckout(string TopLevel, string CommonDir, string MainCheckout)
{
    /// <summary>The checkout <paramref name="startDirectory"/> is in, or null outside git or when its <c>.git</c> file cannot be read.</summary>
    public static GitCheckout? Find(string startDirectory, IPlatform platform)
    {
        if (GitRootLocator.Find(startDirectory, platform) is not { } topLevel)
            return null;

        topLevel = Path.TrimEndingDirectorySeparator(topLevel);
        string dotGit = Path.Combine(topLevel, ".git");
        string? commonDir = platform.FileExists(dotGit)
            ? GitDirOf(dotGit, topLevel, platform) is { } gitDir ? CommonDirOf(gitDir, platform) : null
            : dotGit;
        if (commonDir is null)
            return null;

        string mainCheckout = Path.GetFileName(commonDir) == ".git" && Path.GetDirectoryName(commonDir) is { Length: > 0 } parent ? parent : topLevel;
        return new GitCheckout(topLevel, commonDir, mainCheckout);
    }

    // `gitdir: <path>`, absolute, or relative to the directory the file sits in (worktree.useRelativePaths).
    private static string? GitDirOf(string dotGitFile, string topLevel, IPlatform platform)
    {
        if (platform.ReadRegularFileText(dotGitFile, out _) is not { } text)
            return null;

        const string prefix = "gitdir:";
        string line = text.Split('\n')[0].Trim();
        return line.StartsWith(prefix, StringComparison.Ordinal) && line[prefix.Length..].Trim() is { Length: > 0 } path
            ? Normalize(Path.GetFullPath(path, topLevel))
            : null;
    }

    // A linked worktree's git dir names the shared one in `commondir` (`../..`, relative to itself); a git dir without one is its own.
    private static string CommonDirOf(string gitDir, IPlatform platform)
    {
        string commonDirFile = Path.Combine(gitDir, "commondir");
        return platform.FileExists(commonDirFile) && platform.ReadRegularFileText(commonDirFile, out _) is { } text && text.Trim() is { Length: > 0 } relative
            ? Normalize(Path.GetFullPath(relative, gitDir))
            : gitDir;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);
}
