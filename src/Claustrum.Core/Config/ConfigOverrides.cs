namespace Claustrum.Core.Config;

// Flat bag of every CLI/MCP flag that can override config (A5 surface). Config.Resolve only reads
// Backend/Model/Effort/Permission/Deny; the rest (budget, timeout, env passthrough) flows into
// RunRequest/RunOptions directly and is carried here so callers have one override type to build.
public sealed record ConfigOverrides(
    string? Backend = null,
    string? Model = null,
    string? Effort = null,
    string? Permission = null,
    string[]? Deny = null,
    decimal? BudgetUsd = null,
    int? TimeoutSeconds = null,
    bool? EnvPassthroughAll = null,
    Dictionary<string, string>? Env = null);
