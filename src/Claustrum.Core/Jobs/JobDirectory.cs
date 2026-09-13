using Claustrum.Core.Platform;

namespace Claustrum.Core.Jobs;

// `~/.claustrum/jobs/<yyyyMMdd-HHmmss-4hex>/` (docs/PLAN.md A4); `CLAUSTRUM_HOME` relocates the
// root, e.g. for parallel worktree jobs that must not share one job tree.
public static class JobDirectory
{
    public static JobPaths Create(IPlatform platform)
    {
        string root = ResolveRoot(platform);
        string id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0, 0x10000):x4}";
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);

        return new JobPaths(
            id,
            directory,
            Path.Combine(directory, "request.json"),
            Path.Combine(directory, "system.md"),
            Path.Combine(directory, "stdout.log"),
            Path.Combine(directory, "stderr.log"),
            Path.Combine(directory, "result.json"));
    }

    // Public so `claustrum jobs list|show|logs` (CLI, builder slice 2026-09-13) can find existing job
    // directories without creating a new one.
    public static string ResolveRoot(IPlatform platform) =>
        platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "jobs")
            : Path.Combine(platform.HomeDirectory, ".claustrum", "jobs");
}
