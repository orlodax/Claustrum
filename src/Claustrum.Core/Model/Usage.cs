namespace Claustrum.Core.Model;

// CacheReadInputTokens/CacheCreationInputTokens are additive over docs/PLAN.md A2's original sketch
// (claude's `usage` object carries both); schema_version stays "1" per NOTES.md "Report extraction
// shape"'s precedent for additive RunResult fields.
public sealed record Usage(int? InputTokens, int? OutputTokens, int? CacheReadInputTokens, int? CacheCreationInputTokens);
