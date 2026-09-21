namespace Claustrum.Core.Jobs;

// The half of a BudgetLedger admission that keeps a running job's cap reserved: an exclusive handle
// on `<jobId>.live`, held for the job's whole lifetime. A sibling's admission cannot see a process,
// but it can try to open this file — the exclusive-open trick RoleConcurrencyGate uses for its slots,
// read backwards. Until it is completed the entry stays open and counts as its cap, not as $0
// (NOTES.md "Tree budget accounting is a file ledger").
public sealed class BudgetReservation : IAsyncDisposable
{
    private readonly string directory;
    private readonly string livePath;
    private readonly FileStream live;

    internal BudgetReservation(string directory, string livePath, string treeId, string jobId, decimal cap, FileStream live)
    {
        this.directory = directory;
        this.livePath = livePath;
        this.live = live;
        TreeId = treeId;
        JobId = jobId;
        Cap = cap;
    }

    public string TreeId { get; }
    public string JobId { get; }

    /// <summary>The slice the ledger granted this job, and what it is charged if no cost is reported.</summary>
    public decimal Cap { get; }

    /// <summary>
    /// Closes this job's entry under the ledger lock and releases the reservation. The charge is
    /// <paramref name="cost"/> when the backend reported one, the granted cap when it did not but the
    /// process <paramref name="ran"/> anyway — cursor and copilot report no cost at all, and an entry
    /// closing at $0 would make the tree cap a no-op — and $0 when nothing ever ran.
    /// </summary>
    public async Task CompleteAsync(decimal? cost, bool ran)
    {
        await BudgetLedger.CloseAsync(directory, JobId, cost ?? (ran ? Cap : 0m));

        // Only after the entry is finished, and in this order: a finished entry is never probed, so
        // releasing the handle here can no longer be mistaken for a holder that died.
        live.Dispose();
        TryDeleteLive();
    }

    // Releasing the handle is the whole contract. An entry still open when its holder lets go is
    // exactly what the next admission turns into `abandoned`, so a hard kill and a forgotten
    // CompleteAsync converge on the same $0 instead of freezing a slice of the tree forever.
    public ValueTask DisposeAsync()
    {
        live.Dispose();
        return ValueTask.CompletedTask;
    }

    // Best-effort: a `.live` file left behind costs nothing — its entry is finished, so nobody probes
    // it again — and deleting it is not worth failing a run that has already produced its result.
    private void TryDeleteLive()
    {
        try
        {
            File.Delete(livePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
