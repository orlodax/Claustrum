using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Claustrum.Roles.Tests;

// macOS hands out temp paths under /var, a link to /private/var, and every fence and snapshot reports real paths, so a
// test comparing a typed temp path with a resolved one failed there (PR #116, first macOS CI run, 2026-10-14). The temp
// directory is pinned to its real path once, before any test runs; spawned processes inherit it.
internal static class RealTempPath
{
#pragma warning disable CA2255 // A test assembly is the place this module initializer is meant for.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Pin()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        ProcessStartInfo start = new("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cd \"$1\" && pwd -P");
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add(Path.GetTempPath());
        using Process? process = Process.Start(start);
        string? real = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();
        if (!string.IsNullOrEmpty(real))
            Environment.SetEnvironmentVariable("TMPDIR", real + "/");
    }
}
