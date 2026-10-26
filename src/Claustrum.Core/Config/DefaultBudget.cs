namespace Claustrum.Core.Config;

// One layer's defaults.budget_usd, nullable on DefaultsSettings the way CastBudget is: a null DefaultBudget? means
// the layer leaves the key out and the earlier layer's cap stands; Usd null means the layer turns the default cap off
// (`"budget_usd": null`, CLAUSTRUM_BUDGET_USD=none). NOTES.md "`defaults.budget_usd: null` turns the default cap off".
public readonly record struct DefaultBudget(decimal? Usd);
