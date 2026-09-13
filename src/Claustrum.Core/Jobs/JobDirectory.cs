using Claustrum.Core.Platform;
// Alias, not `using Claustrum.Core.Config;`: from this file's namespace (a sibling of `Config` under
// `Claustrum.Core`), the *namespace* `Claustrum.Core.Config` shadows the `Config` *type* inside it —
// an unqualified `Config.Load(...)` binds to the namespace and fails to compile.
using CoreConfig = Claustrum.Core.Config.Config;

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

        Prune(platform, root);

        return new JobPaths(
            id,
            directory,
            Path.Combine(directory, "request.json"),
            Path.Combine(directory, "system.md"),
            Path.Combine(directory, "stdout.log"),
            Path.Combine(directory, "stderr.log"),
            Path.Combine(directory, "result.json"));
    }

    // jobs.keep_last (docs/PLAN.md A7, review finding #5). The job store is one global
    // `~/.claustrum/jobs` tree regardless of which repo a run happens in (ResolveRoot below never
    // takes a cwd), so pruning deliberately reads only the non-repo config layers (builtin/user/env)
    // — a repo's own claustrum.json can't scope a policy over another repo's job history anyway.
    private static void Prune(IPlatform platform, string root)
    {
        int keepLast = CoreConfig.Load(platform, platform.HomeDirectory).Merged.Jobs?.KeepLast ?? 200;
        IEnumerable<string> stale = Directory.EnumerateDirectories(root)
            .OrderDescending(StringComparer.Ordinal)
            .Skip(keepLast);

        foreach (string directory in stale)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort: a directory still open (e.g. a log handle) is left for the next prune.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Public so `claustrum jobs list|show|logs` (CLI, builder slice 2026-09-13) can find existing job
    // directories without creating a new one.
    public static string ResolveRoot(IPlatform platform) =>
        platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "jobs")
            : Path.Combine(platform.HomeDirectory, ".claustrum", "jobs");
}
