namespace Claustrum.Core.Jobs;

// BudgetLedger.AdmitAsync's answer. Spent is what finished siblings cost and Reserved what live ones
// still hold; EffectiveCap is the slice this job may spend and Reservation the `.live` handle that
// keeps it reserved — both null exactly when the job was refused, and Reason is then the message that
// reaches RunResult.Error. An admitted Reservation is owned by the caller: complete it, then dispose.
public sealed record BudgetAdmission(
    bool Admitted,
    decimal Spent,
    decimal Reserved,
    decimal Remaining,
    decimal? EffectiveCap,
    string? Reason,
    BudgetReservation? Reservation);
