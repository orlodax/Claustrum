namespace Claustrum.Casts;

// Model/Backend follow the same "[backend:]model-id or alias" grammar as claustrum.json (§A7):
// they win over a repo's claustrum.json roles.<role> settings but lose to an explicit --model/
// --backend flag (DelegateEngine.ApplyCast). Tier picks which role.json tier (high/xhigh/max) to
// render, same precedence as --tier. MaxParallel (docs/PLAN.md §D1 "max_parallel": 3) is a
// concurrency cap at any number, 1 included (#58), and turns on git worktree isolation once it
// exceeds 1 (DelegateEngine.RunAsync); null means no cap and no isolation.
public sealed record CastRoleEntry(string? Model, string? Backend, string? Tier, int? MaxParallel = null);
