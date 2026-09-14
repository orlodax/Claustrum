namespace Claustrum.Casts;

// Model/Backend follow the same "[backend:]model-id or alias" grammar as claustrum.json (§A7):
// they win over a repo's claustrum.json roles.<role> settings but lose to an explicit --model/
// --backend flag (DelegateEngine.ApplyCast). Tier picks which role.json tier (high/xhigh/max) to
// render, same precedence as --tier.
public sealed record CastRoleEntry(string? Model, string? Backend, string? Tier);
