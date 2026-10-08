namespace Claustrum.Core.Tests.Testing;

/// <summary>
/// Teardown for a temp directory that may contain a real git repository. git writes loose objects
/// read-only (mode 0444, the read-only attribute on Windows), and a plain recursive delete refuses those
/// on Windows with UnauthorizedAccessException — the same trap Claustrum.Tests' own TempTree documents
/// (2026-09-19, nine tests red on windows-latest); duplicated here because the two test projects share no code.
/// </summary>
public static class TempTree
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
