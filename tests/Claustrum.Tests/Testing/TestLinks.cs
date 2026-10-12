namespace Claustrum.Tests.Testing;

/// <summary>
/// Symbolic links for the tests that need one. A machine that will not make a link (Windows without Developer Mode or
/// elevation) skips the case with the reason, rather than failing it; nothing else skips.
/// </summary>
public static class TestLinks
{
    public static void Create(string linkPath, string target)
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

    public static void CreateDirectoryLink(string linkPath, string target)
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
}
