using Claustrum.Core.Config;
using Claustrum.Core.Jobs;

namespace Claustrum.Core;

// Per-call knobs that are not part of the persisted RunRequest: the diff cap differs between the
// CLI (200 KB) and MCP (64 KB) front doors (docs/PLAN.md A2), and OnStreamLine is how `--stream`
// reaches Claustrum's own stderr (A4) — both are call-site concerns, not request data. BackendConfig
// and EnvPassthroughAll are the caller's resolved `claustrum.json` values (`backends.<name>.path`,
// `defaults.env_passthrough`) for the same reason: Runner does not read Config itself (NOTES.md
// "Backend config and env passthrough are call-site data, not RunRequest fields"). `Tree` and
// `Admission` are the same category and carry §D4: the job tree this run belongs to, and — when the
// caller had to admit before Runner — the answer it already holds, documented where they live.
public sealed record RunOptions(
    int DiffByteCapBytes,
    BackendConfig? BackendConfig = null,
    bool EnvPassthroughAll = false,
    Action<string>? OnStreamLine = null,
    JobTreeBudget? Tree = null,
    BudgetAdmission? Admission = null);
