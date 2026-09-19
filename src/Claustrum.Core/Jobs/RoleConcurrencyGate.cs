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

    /// <summary>
    /// Waits for one of <paramref name="maxParallel"/> slots named by <paramref name="key"/>, for at
    /// most <paramref name="timeout"/>. Build the key with <see cref="KeyFor"/>: the pool is a set of
    /// files on disk shared by every process in the repo, so two casts naming the same role with
    /// different caps must not draw from one pool.
    /// </summary>
    public static async Task<RoleConcurrencyGate> AcquireAsync(string cwd, string key, int maxParallel, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);

        string directory = Path.Combine(cwd, ".claustrum", "locks");
        Directory.CreateDirectory(directory);

        // A bounded wait, not `while (true)`: a sibling builder that hangs holds its slot until its
        // own timeout, and an unbounded poll turned that into a `claustrum run` with no output and no
        // end — the run's own --timeout lives inside Runner and never reaches this point.
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        IOException? lastFailure = null;
        while (true)
        {
            for (int slotIndex = 0; slotIndex < maxParallel; slotIndex++)
            {
                string path = Path.Combine(directory, $"{key}.{slotIndex}.lock");
                try
                {
                    return new RoleConcurrencyGate(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex)
                {
                    // Contention and a genuinely broken directory (read-only mount, full disk, dead
                    // network share) arrive as the same IOException on Unix and cannot be told apart
                    // here. The deadline below is what stops the second case from polling forever
                    // (review finding); carrying the last failure out names the real cause.
                    lastFailure = ex;
                }
            }

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException(
                    $"all {maxParallel} '{key}' slots under '{directory}' stayed unavailable for {timeout.TotalSeconds:0}s "
                    + $"(last error: {lastFailure?.Message}) — a sibling run may be stuck; check `claustrum jobs list`",
                    lastFailure);

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// The slot-pool name for one cast's role. docs/PLAN.md §D4 caps per cast, not per role: keying
    /// on the role alone let a second cast with a larger max_parallel for the same role widen the
    /// first cast's cap, since both drew from one set of files.
    /// </summary>
    public static string KeyFor(string? castName, string role) =>
        $"{Sanitize(castName is { Length: > 0 } ? castName : "default")}__{Sanitize(role)}";

    // The lock file itself is never deleted: unlinking it while another process races to reopen the
    // same path would let that process's lock end up on an orphaned inode while a third process opens
    // a fresh file at the same path and takes the "same" slot — double-booking it. Leaving the (empty)
    // file in place forever is cheap and keeps the exclusivity check meaningful for the life of the repo.
    public ValueTask DisposeAsync()
    {
        slot.Dispose();
        return ValueTask.CompletedTask;
    }

    // Cast and role names reach the filesystem here, so anything that is not obviously safe in a file
    // name collapses to '_'. Both halves are sanitized, so the "__" join stays unambiguous.
    private static string Sanitize(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = char.IsLetterOrDigit(source[i]) || source[i] is '-' or '.' ? source[i] : '_';
        });
}
