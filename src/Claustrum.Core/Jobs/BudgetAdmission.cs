namespace Claustrum.Core.Jobs;

// BudgetLedger.Admit's answer. EffectiveCap is the slice this job may spend — the requested cap
// clamped to Remaining, or all of Remaining when none was requested — and is null exactly when the
// job was refused; Reason is then the message that reaches RunResult.Error.
public sealed record BudgetAdmission(bool Admitted, decimal Spent, decimal Remaining, decimal? EffectiveCap, string? Reason);
