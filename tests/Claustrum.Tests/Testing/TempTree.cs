namespace Claustrum.Tests.Testing;

/// <summary>
/// Teardown for a temp directory that may contain a real git repository.
/// </summary>
/// <remarks>
/// git creates loose objects read-only (mode 0444, which git-for-windows maps to the Windows
/// read-only attribute), and <see cref="Directory.Delete(string, bool)"/> refuses a read-only file on
/// Windows with UnauthorizedAccessException — so a plain recursive delete tore down these trees fine
/// on Linux and failed nine tests on windows-latest the first time that leg got far enough to run
/// them (2026-09-19, job 105913346697: "Access to the path '4ef53184…' is denied"). Clearing the
/// attribute first is the portable teardown; on Unix the loop finds nothing to clear.
/// </remarks>
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
