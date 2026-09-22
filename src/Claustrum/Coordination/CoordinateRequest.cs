using Claustrum.Core.Config;

namespace Claustrum.Coordination;

// What both front doors (`claustrum coordinate`, MCP `coordinate`) have finished parsing before
// CoordinateEngine takes over — the same split DelegateRequest makes for `run`/`delegate`. Exactly
// one of Issues/Brief carries the task; CoordinateEngine, not the caller, enforces that.
public sealed record CoordinateRequest(
    string Cwd,
    string? CastName,
    int[] Issues,
    string? Brief,
    string? TierFlag,
    ConfigOverrides Overrides,
    bool Stream,
    int DiffCapBytes,
    Action<string>? OnStreamLine = null);
