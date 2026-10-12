namespace Claustrum.Roles.Tests.Sync;

// Symbolic links for the sync tests (#95). A machine that will not make one (Windows without Developer Mode or
// elevation) skips the case with the reason; nothing else skips. Paths compare the way the OS does.
internal static class LinkFixtures
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void DirectoryLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }
    }

    public static void FileLink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }
    }

    /// <summary>Every entry under <paramref name="directory"/>, sorted, so a before/after pair shows anything created or removed.</summary>
    public static string[] Snapshot(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(PathComparer)];
}
