namespace Claustrum.Core.Jobs;

// Cross-process, cross-platform concurrency cap for one cast role (docs/PLAN.md §D4 "max_parallel is
// enforced by a per-cast semaphore ... both CLI and MCP paths go through it"). An in-memory
// SemaphoreSlim only coordinates callers inside one process, but a spawned architect fans builders out
// as separate `claustrum run` CLI processes, so the cap has to live on disk: up to `maxParallel`
// numbered lock files under `.claustrum/locks/`, each held open with FileShare.None for as long as a
// slot is in use. Opening one is the acquire; a process that finds every slot taken polls until one
// frees. This mirrors the exclusive-open trick GitProcess's callers already rely on git itself for,
// rather than reaching for an OS-specific named semaphore.
public sealed class RoleConcurrencyGate : IAsyncDisposable
{
    private static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(50);

    private readonly FileStream slot;

    private RoleConcurrencyGate(FileStream slot)
    {
        this.slot = slot;
    }

    public static async Task<RoleConcurrencyGate> AcquireAsync(string cwd, string key, int maxParallel, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(cwd, ".claustrum", "locks");
        Directory.CreateDirectory(directory);

        while (true)
        {
            for (int slotIndex = 0; slotIndex < maxParallel; slotIndex++)
            {
                string path = Path.Combine(directory, $"{key}.{slotIndex}.lock");
                try
                {
                    return new RoleConcurrencyGate(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException)
                {
                    // Slot held by another process/task; try the next slot.
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    // The lock file itself is never deleted: unlinking it while another process races to reopen the
    // same path would let that process's lock end up on an orphaned inode while a third process opens
    // a fresh file at the same path and takes the "same" slot — double-booking it. Leaving the (empty)
    // file in place forever is cheap and keeps the exclusivity check meaningful for the life of the repo.
    public ValueTask DisposeAsync()
    {
        slot.Dispose();
        return ValueTask.CompletedTask;
    }
}
