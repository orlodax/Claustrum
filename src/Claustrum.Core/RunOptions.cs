namespace Claustrum.Core;

// Per-call knobs that are not part of the persisted RunRequest: the diff cap differs between the
// CLI (200 KB) and MCP (64 KB) front doors (docs/PLAN.md A2), and OnStreamLine is how `--stream`
// reaches Claustrum's own stderr (A4) — both are call-site concerns, not request data.
public sealed record RunOptions(int DiffByteCapBytes, Action<string>? OnStreamLine = null);
