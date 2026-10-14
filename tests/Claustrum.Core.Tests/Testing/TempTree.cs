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

        // Windows can keep a directory busy for a moment after a child process in it has exited (PR #109 CI,
        // 2026-10-14: "being used by another process" right after a cancelled git), so a busy delete is retried.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (OperatingSystem.IsWindows() && attempt < 10)
            {
                Thread.Sleep(200);
            }
        }
    }
}
