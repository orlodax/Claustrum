using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Json;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Jobs;

// docs/PLAN.md §D4: "budget_usd is enforced across the job tree: a child that would exceed the
// remaining budget is refused with status BudgetExceeded". The tree is whatever CLAUSTRUM_PARENT_JOB
// names (§D2), and its members are separate `claustrum run` processes, so the running total lives on
// disk beside the job store: one `<jobId>.json` entry per job under `<claustrum home>/budget/<tree>/`,
// guarded by the same exclusive-open `.lock` trick RoleConcurrencyGate uses for its slots. NOTES.md
// "Tree budget accounting is a file ledger" holds the two gaps this shape accepts.
public static class BudgetLedger
{
    private const string TreeVariable = "CLAUSTRUM_PARENT_JOB";
    private const string UnknownRole = "unknown";

    private static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan lockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The job tree this process belongs to, or null when it is not in one — docs/PLAN.md §D2:
    /// CLAUSTRUM_PARENT_JOB links a child job to the `coordinate` job that spawned it. The value is
    /// taken as given and never checked against the job store: a tree is an accounting bucket, not a
    /// job that has to exist (a host architect may export the variable by hand).
    /// </summary>
    public static string? TreeIdFor(IPlatform platform) =>
        platform.GetEnvironmentVariable(TreeVariable)?.Trim() is { Length: > 0 } treeId ? treeId : null;

    /// <summary>
    /// The ledger directory for one tree: its id under `budget/` in the same claustrum home the job
    /// store uses (JobDirectory.ResolveHome), so CLAUSTRUM_HOME relocates both together.
    /// </summary>
    public static string DirectoryFor(IPlatform platform, string treeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeId);

        // Sanitize leaves dots alone (legal in a name), but a bare "." or ".." would name the budget
        // root or the home above it instead of a tree, so an all-dots id loses its dots too.
        string safe = Sanitize(treeId);
        return Path.Combine(JobDirectory.ResolveHome(platform), "budget", safe.Trim('.').Length > 0 ? safe : safe.Replace('.', '_'));
    }

    /// <summary>
    /// Claims this job's slice of the tree budget, or refuses it. Under the ledger lock: spend is the
    /// sum of the costs recorded so far, and the granted cap is <paramref name="requestedCap"/>
    /// clamped to what is left (all of what is left when no cap was requested). Refused when nothing
    /// is left, or when an explicit cap asks for more than that.
    /// </summary>
    public static BudgetAdmission Admit(IPlatform platform, string treeId, string jobId, string role, decimal treeBudget, decimal? requestedCap)
    {
        string directory = DirectoryFor(platform, treeId);
        Directory.CreateDirectory(directory);

        using FileStream guard = Lock(directory);

        decimal spent = ReadEntries(directory).Sum(entry => entry.Cost ?? 0m);
        decimal remaining = treeBudget - spent;
        string state = $"tree '{treeId}': {Dollars(spent)} of {Dollars(treeBudget)} already spent, {Dollars(remaining)} remaining";

        if (remaining <= 0)
            return new BudgetAdmission(Admitted: false, Spent: spent, Remaining: remaining, EffectiveCap: null,
                Reason: $"{state}; nothing left for role '{role}'");

        if (requestedCap is { } cap && cap > remaining)
            return new BudgetAdmission(Admitted: false, Spent: spent, Remaining: remaining, EffectiveCap: null,
                Reason: $"{state}; --budget {Money(cap)} exceeds it");

        // Min, though the branch above already refused a larger request: it states the invariant the
        // cap rests on, that no admitted job is handed more than the tree has left.
        decimal effectiveCap = Math.Min(requestedCap ?? remaining, remaining);
        Write(EntryPath(directory, jobId), new BudgetLedgerEntry(jobId, role, effectiveCap, Cost: null, DateTimeOffset.UtcNow, FinishedAt: null));

        return new BudgetAdmission(Admitted: true, Spent: spent, Remaining: remaining, EffectiveCap: effectiveCap, Reason: null);
    }

    /// <summary>
    /// Closes this job's entry with what the run actually cost (null when the backend reported none).
    /// A job that never gets here leaves its entry open, and an open entry counts as $0.
    /// </summary>
    public static void Record(IPlatform platform, string treeId, string jobId, decimal? cost)
    {
        string directory = DirectoryFor(platform, treeId);
        Directory.CreateDirectory(directory);

        using FileStream guard = Lock(directory);

        // A missing entry (a hand-cleaned ledger, a job id Admit never saw) is written rather than
        // thrown over: not recording a finished job's cost is the error that overstates what the tree
        // has left, so it is the one outcome worth defending against here.
        string path = EntryPath(directory, jobId);
        BudgetLedgerEntry entry = TryRead(path)
            ?? new BudgetLedgerEntry(jobId, UnknownRole, Cap: null, Cost: null, DateTimeOffset.UtcNow, FinishedAt: null);

        Write(path, entry with { Cost = cost, FinishedAt = DateTimeOffset.UtcNow });
    }

    private static IEnumerable<BudgetLedgerEntry> ReadEntries(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Select(TryRead).OfType<BudgetLedgerEntry>();

    // A truncated entry (a process killed mid-write) must not take the whole admission down — the same
    // call `jobs list` makes for a partial result.json. It then counts as $0, like an open one.
    private static BudgetLedgerEntry? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), ClaustrumJsonContext.Default.BudgetLedgerEntry)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void Write(string path, BudgetLedgerEntry entry) =>
        File.WriteAllText(path, JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.BudgetLedgerEntry));

    private static string EntryPath(string directory, string jobId) => Path.Combine(directory, $"{Sanitize(jobId)}.json");

    // Remaining goes negative whenever a child outspent its cap (a backend may overshoot its own
    // --max-budget-usd), so the sign leads the amount rather than sitting between it and the '$'.
    private static string Dollars(decimal value) => value < 0 ? $"-${Money(-value)}" : $"${Money(value)}";

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // Reading the ledger, deciding, and writing an entry is one critical section across processes, so
    // it needs a real mutex: whoever opens `.lock` with FileShare.None holds it and the rest poll.
    // Never deleted, for the reason RoleConcurrencyGate never deletes its slot files — unlinking it
    // lets a rival's lock land on an orphaned inode while a third process opens a fresh file at the
    // same path and believes it holds the same lock. The wait is bounded: one stuck holder must not
    // turn every other child of the tree into a run with no output and no end.
    private static FileStream Lock(string directory)
    {
        string path = Path.Combine(directory, ".lock");
        long deadline = Environment.TickCount64 + (long)lockTimeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (Environment.TickCount64 >= deadline)
            {
                // Contention and a genuinely broken directory (read-only mount, full disk) arrive as
                // the same IOException on Unix and cannot be told apart here; the deadline is what
                // stops the second case from polling forever, and `ex` names the real cause.
                throw new TimeoutException(
                    $"the budget ledger lock under '{directory}' stayed unavailable for {lockTimeout.TotalSeconds:0}s "
                    + $"(last error: {ex.Message}) — another job of this tree may be stuck; check `claustrum jobs list`",
                    ex);
            }
            catch (IOException)
            {
                // Another job of this tree holds the ledger; there is still time, so poll.
            }

            Thread.Sleep(pollInterval);
        }
    }

    // A tree id becomes a directory name, so anything not obviously safe collapses to '_' — the same
    // collapse RoleConcurrencyGate applies to its own keys, copied rather than shared so this slice
    // leaves the gate untouched. Two ids differing only in unsafe characters share one ledger; a real
    // tree id is a job id (`yyyyMMdd-HHmmss-8hex`) and passes through unchanged.
    private static string Sanitize(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = char.IsLetterOrDigit(source[i]) || source[i] is '-' or '.' ? source[i] : '_';
        });
}
