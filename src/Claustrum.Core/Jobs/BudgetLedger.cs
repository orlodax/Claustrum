using System.Globalization;
using System.Text.Json;
using Claustrum.Core.Json;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Jobs;

// docs/PLAN.md §D4: "budget_usd is enforced across the job tree: a child that would exceed the
// remaining budget is refused with status BudgetExceeded". The tree is whatever CLAUSTRUM_PARENT_JOB
// names (§D2), and its members are separate `claustrum run` processes, so the running total lives on
// disk beside the job store: per job a `<jobId>.json` entry plus a `<jobId>.live` handle held open for
// as long as it runs, under `<claustrum home>/budget/<tree>/` and guarded by the same exclusive-open
// `.lock` trick RoleConcurrencyGate uses for its slots. NOTES.md "Tree budget accounting is a file
// ledger" carries the admission rule in full and what the `.live` probe is worth.
public static class BudgetLedger
{
    /// <summary>
    /// docs/PLAN.md §D2's tree variable by name: read here to decide membership, written by
    /// `coordinate` on its architect's request env, and the one name `env_passthrough: "all"` still
    /// filters out of an inherited environment (EnvAllowList).
    /// </summary>
    public const string TreeVariable = "CLAUSTRUM_PARENT_JOB";

    private const string UnknownRole = "unknown";
    private const string LiveExtension = ".live";

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

    /// <summary>`$0.10`, and `-$0.50` for a remainder a backend overshot — the sign leads the amount.</summary>
    public static string Dollars(decimal value) => value < 0 ? $"-${Money(-value)}" : $"${Money(value)}";

    /// <summary>
    /// Claims this job's slice of the tree budget, or refuses it. Everything happens under the ledger
    /// lock: a live sibling's granted cap counts against the tree exactly like a finished one's cost,
    /// and the cap granted here is <paramref name="requestedCap"/> — or an equal share of what is
    /// left, for a role that fans out — clamped to the remainder and floored to whole cents. The
    /// admission owns a <see cref="BudgetReservation"/> that must be completed and disposed: while it
    /// is open, this job's cap stays reserved against every other child of the tree. This is the only
    /// binding call: whoever must decide before spending anything (a worktree, a branch) calls it
    /// itself and hands the outcome on, rather than peeking and asking again later.
    /// </summary>
    public static async Task<BudgetAdmission> AdmitAsync(IPlatform platform, JobTreeBudget tree, string jobId, string role, decimal? requestedCap)
    {
        string directory = DirectoryFor(platform, tree.TreeId);
        Directory.CreateDirectory(directory);

        using FileStream guard = await LockAsync(directory);

        BudgetLedgerState state = Survey(directory, markAbandoned: true);
        decimal remaining = tree.BudgetUsd - state.Spent - state.Reserved;
        string summary = $"tree '{tree.TreeId}': {Dollars(state.Spent)} spent + {Dollars(state.Reserved)} reserved "
            + $"of {Dollars(tree.BudgetUsd)}, {Dollars(remaining)} remaining";

        // A role without max_parallel divides by 1, so its first child reserves the whole remainder and
        // its second finds nothing. --budget is no way out *here*: it is the sibling already holding
        // the reservation that would have had to ask for less, and an exhausted tree refuses whatever
        // this job passes. What can still come back is that sibling's slice, so name the wait instead.
        if (remaining <= 0)
        {
            int running = state.Rows.Count(row => row.State == BudgetEntryState.Running);
            string held = state.Reserved > 0
                ? $" while {running} running job(s) hold {Dollars(state.Reserved)} — wait for one to finish"
                : "";
            return Refused(state, remaining, $"{summary}; nothing left for role '{role}'{held}");
        }

        if (requestedCap is { } cap && cap > remaining)
            return Refused(state, remaining, $"{summary}; --budget {Money(cap)} exceeds it");

        // Share is the role's max_parallel: siblings that start together each take a slice of the
        // remainder instead of the whole of it, so none can starve the others before it even runs. An
        // explicit --budget is the caller's own ceiling and is never divided, only clamped. Cents,
        // floored: the exact quotient (`0.6666666666666666666666666667`) used to reach
        // --max-budget-usd and request.json verbatim, and rounding *down* is the only direction that
        // cannot over-grant.
        decimal effectiveCap = Cents(Math.Min(requestedCap ?? remaining / tree.Share, remaining));
        if (effectiveCap <= 0)
        {
            // The tree is not empty here — only this job's slice is, so --budget is real advice. The
            // requested cap prints unrounded (Money would show the `0.00` that is being complained about).
            string vanished = requestedCap is { } requested
                ? $"--budget {requested.ToString(CultureInfo.InvariantCulture)} rounds to $0.00"
                : $"the slice for role '{role}' ({Dollars(remaining)} / {tree.Share}) rounds to $0.00 "
                    + $"— pass --budget (at most {Dollars(remaining)}) to claim an explicit slice";
            return Refused(state, remaining, $"{summary}; {vanished}");
        }

        BudgetReservation reservation = Claim(directory, tree.TreeId, jobId, role, effectiveCap);

        return new BudgetAdmission(Admitted: true, state.Spent, state.Reserved, remaining, effectiveCap, Reason: null, reservation);
    }

    /// <summary>
    /// What the tree has left right now: the admission's arithmetic without its decision, and without
    /// writing anything at all — a report, for a caller that only wants to show the number.
    /// ⚠ Read-only means non-binding: a sibling's reservation is released the moment it finishes, so a
    /// remainder read here can climb back before the next <see cref="AdmitAsync"/>. Nothing may be
    /// skipped on the strength of it (2026-09-21: gating worktree isolation on a `&lt;= 0` peek ran a
    /// job directly in the caller's cwd, un-isolated and outside the concurrency cap, in that window).
    /// </summary>
    public static async Task<decimal> PeekRemainingAsync(IPlatform platform, JobTreeBudget tree)
    {
        BudgetLedgerState state = await ReadAsync(platform, tree.TreeId);
        return tree.BudgetUsd - state.Spent - state.Reserved;
    }

    /// <summary>
    /// Every entry of one tree with the liveness its `.live` probe reports now, plus the two sums the
    /// admission rule runs on (`claustrum jobs budget`). Read-only: a dead holder's entry is reported
    /// as abandoned but left alone on disk, which is the next admission's job to rewrite.
    /// </summary>
    public static async Task<BudgetLedgerState> ReadAsync(IPlatform platform, string treeId)
    {
        string directory = DirectoryFor(platform, treeId);
        if (!Directory.Exists(directory))
            return new BudgetLedgerState(directory, [], Spent: 0m, Reserved: 0m);

        using FileStream guard = await LockAsync(directory);

        return Survey(directory, markAbandoned: false);
    }

    // BudgetReservation.CompleteAsync's other half: the final entry, written under the lock. A missing
    // entry (a hand-cleaned ledger, a job id no admission wrote) is created rather than thrown over —
    // losing a finished job's cost is the one error that overstates what the tree has left. Abandoned
    // is cleared: a real completion outranks a sibling's guess that this holder had died.
    internal static async Task CloseAsync(string directory, string jobId, decimal cost)
    {
        Directory.CreateDirectory(directory);

        using FileStream guard = await LockAsync(directory);

        string path = EntryPath(directory, jobId);
        BudgetLedgerEntry entry = TryRead(path)
            ?? new BudgetLedgerEntry(jobId, UnknownRole, Cap: null, Cost: null, DateTimeOffset.UtcNow, FinishedAt: null);

        Write(path, entry with { Cost = cost, FinishedAt = DateTimeOffset.UtcNow, Abandoned = false });
    }

    // The three ways an entry can count, decided by one probe: a finished entry contributes its cost,
    // one still holding its `.live` handle contributes the cap it was granted, and an open entry whose
    // handle nobody holds belonged to a job that died — worth $0 from here on, and rewritten as
    // finished so no later pass has to probe it again. Only a caller allowed to write asks for that
    // rewrite: a read-only peek reports the same state and leaves the file alone.
    private static BudgetLedgerState Survey(string directory, bool markAbandoned)
    {
        decimal spent = 0m;
        decimal reserved = 0m;
        List<BudgetLedgerRow> rows = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (TryRead(path) is not { } entry)
                continue;

            if (entry.FinishedAt is not null || entry.Abandoned)
            {
                spent += entry.Cost ?? 0m;
                rows.Add(new BudgetLedgerRow(entry, entry.Abandoned ? BudgetEntryState.Abandoned : BudgetEntryState.Done));
            }
            else if (IsLive(path))
            {
                reserved += entry.Cap ?? 0m;
                rows.Add(new BudgetLedgerRow(entry, BudgetEntryState.Running));
            }
            else
            {
                BudgetLedgerEntry abandoned = entry with { Cost = null, FinishedAt = DateTimeOffset.UtcNow, Abandoned = true };
                if (markAbandoned)
                    Write(path, abandoned);

                rows.Add(new BudgetLedgerRow(abandoned, BudgetEntryState.Abandoned));
            }
        }

        // Ordinal on a `yyyyMMdd-HHmmss-8hex` id is chronological, and the enumeration order of a
        // directory is not a thing `jobs budget` should make its output depend on.
        return new BudgetLedgerState(directory, [.. rows.OrderBy(row => row.Entry.JobId, StringComparer.Ordinal)], spent, reserved);
    }

    // RoleConcurrencyGate's acquire, read backwards: the holder keeps `<jobId>.live` open with
    // FileShare.None for its whole run, so an entry whose file this process CAN open — or that has no
    // file at all — is one whose holder is gone. The probe is instantaneous by design: a live handle
    // fails the open immediately rather than waiting, which is what makes it safe under the lock.
    private static bool IsLive(string entryPath)
    {
        try
        {
            using FileStream probe = new(Path.ChangeExtension(entryPath, LiveExtension), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The order matters: the `.live` handle is taken *before* the entry is written, so no rival can
    // ever read an entry whose holder has not claimed its handle yet and mistake it for a dead one.
    // Both halves happen under the ledger lock, and a rival only probes what it has read.
    private static BudgetReservation Claim(string directory, string treeId, string jobId, string role, decimal cap)
    {
        string entryPath = EntryPath(directory, jobId);
        string livePath = Path.ChangeExtension(entryPath, LiveExtension);
        FileStream live = new(livePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Write(entryPath, new BudgetLedgerEntry(jobId, role, cap, Cost: null, DateTimeOffset.UtcNow, FinishedAt: null));
            return new BudgetReservation(directory, livePath, treeId, jobId, cap, live);
        }
        catch
        {
            live.Dispose();
            throw;
        }
    }

    // A truncated entry (a process killed mid-write) must not take the whole admission down — the same
    // call `jobs list` makes for a partial result.json. It then counts as $0, like an abandoned one.
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

    private static BudgetAdmission Refused(BudgetLedgerState state, decimal remaining, string reason) =>
        new(Admitted: false, state.Spent, state.Reserved, remaining, EffectiveCap: null, Reason: reason, Reservation: null);

    // Whole cents, always downwards: Math.Round would hand out a cent nobody has on a sub-cent
    // remainder, and every number the ledger writes is also a number `jobs budget` prints as `0.00`.
    private static decimal Cents(decimal value) => Math.Floor(value * 100m) / 100m;

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // Reading the ledger, deciding, and writing an entry is one critical section across processes, so
    // it needs a real mutex: whoever opens `.lock` with FileShare.None holds it and the rest poll.
    // Never deleted, for the reason RoleConcurrencyGate never deletes its slot files — unlinking it
    // lets a rival's lock land on an orphaned inode while a third process opens a fresh file at the
    // same path and believes it holds the same lock. The wait is bounded: one stuck holder must not
    // turn every other child of the tree into a run with no output and no end.
    private static async Task<FileStream> LockAsync(string directory)
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

            // No CancellationToken on purpose: the wait is already bounded above, and the caller that
            // must write whatever happens is the finish path, whose token has usually already fired.
            await Task.Delay(pollInterval);
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
