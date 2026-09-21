namespace Claustrum.Core.Jobs;

// One BudgetLedger entry: the file `<jobId>.json` under a tree's ledger directory. Cap is the slice
// admission granted, and what the job reserves against the tree while it is still live; Cost is what
// it was finally charged — the cap itself when the backend reported no cost. Abandoned marks an entry
// a later admission closed on behalf of a holder that died: finished, and worth $0 (NOTES.md "Tree
// budget accounting is a file ledger"). Additive, so an older build still reads the file.
public sealed record BudgetLedgerEntry(
    string JobId,
    string Role,
    decimal? Cap,
    decimal? Cost,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Abandoned = false);
