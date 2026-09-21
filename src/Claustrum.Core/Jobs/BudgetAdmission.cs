namespace Claustrum.Core.Jobs;

// BudgetLedger.AdmitAsync's answer. Spent is what finished siblings cost and Reserved what live ones
// still hold; EffectiveCap is the slice this job may spend and Reservation the `.live` handle that
// keeps it reserved — both null exactly when the job was refused, and Reason is then the message that
// reaches RunResult.Error. An admitted Reservation is owned by the caller: complete it, then dispose.
// When the admission is handed to Runner (RunOptions.Admission) that ownership passes with it —
// Runner.FinishAsync completes and disposes it on every path — but only once Runner has been entered:
// a caller that admits early must release it itself if it never gets that far (DelegateEngine's
// isolated path, whose `git worktree add` can throw in between).
public sealed record BudgetAdmission(
    bool Admitted,
    decimal Spent,
    decimal Reserved,
    decimal Remaining,
    decimal? EffectiveCap,
    string? Reason,
    BudgetReservation? Reservation);
