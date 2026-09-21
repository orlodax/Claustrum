namespace Claustrum.Core.Jobs;

// The job tree a run belongs to and the budget shared across it (docs/PLAN.md §D4), carried in
// RunOptions because it is call-site data like BackendConfig: Core never reads CLAUSTRUM_PARENT_JOB
// or a cast itself — DelegateEngine pairs BudgetLedger.TreeIdFor with the cast's budget_usd and
// hands the pair in. No Tree means no tree: the per-run cap alone, exactly as before §D4.
// Share is the role's max_parallel (1 when it does not fan out): how many ways an admission divides
// what is left, so siblings starting together take a slice each instead of the whole remainder.
public sealed record JobTreeBudget(string TreeId, decimal BudgetUsd, int Share = 1);
