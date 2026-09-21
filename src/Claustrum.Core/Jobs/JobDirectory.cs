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
        Directory.CreateDirectory(root);
        (string id, string directory) = CreateUniqueDirectory(root);

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

    // The id must be unique across *processes*, not just within one: M3 fans builders out as N
    // concurrent `claustrum run` processes that all start inside the same wall-clock second, and the
    // id names the job directory, the `claustrum/<id>` branch and the `.claustrum/worktrees/<id>`
    // path. The old 16-bit suffix collided at ~1-in-65536 per same-second pair, and
    // Directory.CreateDirectory is idempotent, so a collision silently gave two jobs one directory to
    // clobber each other's result.json in (review finding). 32 bits plus an atomic CreateNew claim on
    // a marker file inside the candidate directory makes it a retry instead of a corruption.
    private static (string Id, string Directory) CreateUniqueDirectory(string root)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next():x8}";
            string directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            try
            {
                // CreateNew is the only filesystem primitive here that is atomic across processes:
                // whoever creates .claim owns the directory, everyone else retries with a new id.
                using (new FileStream(Path.Combine(directory, ".claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                return (id, directory);
            }
            catch (IOException)
            {
                // Another process already claimed this id; leave its directory alone and re-roll.
            }
        }

        throw new IOException($"could not allocate a unique job id under '{root}' after 16 attempts");
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
    public static string ResolveRoot(IPlatform platform) => Path.Combine(ResolveHome(platform), "jobs");

    // `$CLAUSTRUM_HOME` or `~/.claustrum`: the one root every on-disk store hangs off — `jobs/` above
    // and BudgetLedger's `budget/` — so a test (or a parallel worktree) that relocates the home
    // isolates all of them together instead of one and not the other.
    public static string ResolveHome(IPlatform platform) =>
        platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(platform.HomeDirectory, ".claustrum");
}
