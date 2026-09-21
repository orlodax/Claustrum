namespace Claustrum.Core.Jobs;

// One tree's ledger at a moment in time: every entry with its liveness, and the two sums the
// admission rule runs on — Spent is what finished jobs cost, Reserved is what live ones may still
// spend (their granted cap). What is left is the caller's to compute: the directory does not know
// the tree's budget, and `claustrum jobs budget` is given only a tree id.
public sealed record BudgetLedgerState(string Directory, BudgetLedgerRow[] Rows, decimal Spent, decimal Reserved);
