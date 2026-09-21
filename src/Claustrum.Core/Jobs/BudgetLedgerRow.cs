namespace Claustrum.Core.Jobs;

// One ledger entry as a reader sees it: the record stored on disk plus the liveness the probe found
// for it just now. `claustrum jobs budget` prints these; the admission rule sums them.
public sealed record BudgetLedgerRow(BudgetLedgerEntry Entry, BudgetEntryState State);
