using Claustrum.Core.Platform;

namespace Claustrum.Core.Git;

public static class GitRootLocator
{
    public static string? Find(string startDirectory, IPlatform platform)
    {
        string? current = Path.GetFullPath(startDirectory);
        while (current is not null)
        {
            if (platform.PathExists(Path.Combine(current, ".git")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
