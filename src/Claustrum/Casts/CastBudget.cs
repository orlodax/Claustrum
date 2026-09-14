namespace Claustrum.Casts;

// Wraps Cast.BudgetUsd so DelegateEngine can tell "no cast is active, fall back to claustrum.json's
// defaults.budget_usd" apart from "a cast is active and its budget_usd is null" — the latter means
// unlimited (docs/PLAN.md §D1: "budget_usd: null ⇒ cap disabled") and must NOT fall back further.
// A plain `decimal?` cannot carry that distinction: both cases look like null.
public readonly record struct CastBudget(decimal? Value);
