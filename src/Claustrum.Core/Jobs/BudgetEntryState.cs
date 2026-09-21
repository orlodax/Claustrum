namespace Claustrum.Core.Jobs;

// What the `<jobId>.live` probe says about one ledger entry. Running reserves its cap against the
// tree; Done contributes its cost; Abandoned is a holder that died with its entry still open — worth
// $0, and rewritten as finished by the next admission.
public enum BudgetEntryState
{
    Running,
    Done,
    Abandoned,
}
