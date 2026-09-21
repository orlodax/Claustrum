namespace Claustrum.Core.Jobs;

// One BudgetLedger entry: the file `<jobId>.json` under a tree's ledger directory. Cap is the slice
// admission granted this job, Cost stays null until the job finishes — and a null Cost is counted as
// $0 (NOTES.md "Tree budget accounting is a file ledger" for why that gap is accepted).
public sealed record BudgetLedgerEntry(
    string JobId,
    string Role,
    decimal? Cap,
    decimal? Cost,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);
