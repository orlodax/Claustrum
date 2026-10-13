using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

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

    /// <summary>A junction (`mklink /J`): the directory-link kind a Windows dotfiles setup uses; Directory.CreateSymbolicLink does not make one.</summary>
    public static void Junction(string linkPath, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("a junction is a Windows directory-link kind.");
            return;
        }

        ProcessStartInfo start = new("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{linkPath}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        string? failure = null;
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
                failure = (stderr.Result + stdout.Result).Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            failure = ex.Message;
        }

        if (failure is not null)
            Assert.Skip($"a junction cannot be created here: {failure}");
    }

    // Windows: a recursive Directory.Delete takes a junction for a volume mount point and fails with "The parameter is
    // incorrect" (PR #96 CI, 2026-10-12), so every directory link goes first on its own, which removes only the link.
    public static void DeleteTree(string directory)
    {
        if (OperatingSystem.IsWindows())
            RemoveDirectoryLinks(directory);

        Directory.Delete(directory, recursive: true);
    }

    private static void RemoveDirectoryLinks(string directory)
    {
        foreach (string child in Directory.GetDirectories(directory))
        {
            if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(child);
            else
                RemoveDirectoryLinks(child);
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

    /// <summary>
    /// POSIX: `chmod 000` on <paramref name="directory"/> until the scope is disposed (restore before the tree is deleted).
    /// Skips where that stops nothing: Windows has no modes, and root (or a capability-holding account) lists it anyway,
    /// which the probe measures rather than guessing from the user name.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static IDisposable Lock(string directory)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("POSIX file modes do not exist on Windows.");

        File.SetUnixFileMode(directory, UnixFileMode.None);
        ModeScope scope = new(directory);
        try
        {
            _ = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return scope;
        }

        scope.Dispose();
        Assert.Skip("this account is not stopped by chmod 000 (root, or a capability that bypasses modes).");
        return scope;
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class ModeScope(string directory) : IDisposable
    {
        public void Dispose() => File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Every entry under <paramref name="directory"/>, sorted, so a before/after pair shows anything created or removed.</summary>
    public static string[] Snapshot(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(PathComparer)];
}
