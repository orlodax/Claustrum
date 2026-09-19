using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Delegation;

// Harness-neutral input to DelegateEngine.RunAsync: everything CLI `run` and MCP `delegate` need to
// agree on once each front door has finished its own arg parsing (a brief already read from
// --brief-file/stdin on the CLI side, "K=V" pairs already split, etc. — see docs/PLAN.md A1 "one
// executable; MCP and CLI are thin adapters over Core", extended one layer up now that both share
// this pipeline instead of duplicating it).
public sealed record DelegateRequest(
    string Role,
    string Brief,
    string Cwd,
    string Tier,
    ConfigOverrides Overrides,
    string? ResumeSession,
    string[] AttachFiles,
    Dictionary<string, string> Env,
    bool Stream,
    int DiffCapBytes,
    CastBudget? CastBudget = null,
    int? MaxParallel = null,
    // The cast MaxParallel came from (CastApplication.Resolve). Only RoleConcurrencyGate reads it:
    // its slot pool has to be per cast, not per role (docs/PLAN.md §D4).
    string? CastName = null,
    Action<string>? OnStreamLine = null);
