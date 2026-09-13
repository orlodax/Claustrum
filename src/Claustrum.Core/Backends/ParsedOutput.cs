using System.Text.Json;
using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

// ReportedEdits is a hint only; WorktreeSnapshot's git diff is truth (docs/PLAN.md A2).
public sealed record ParsedOutput(
    string FinalMessage,
    string? SessionId,
    decimal? CostUsd,
    Usage? Usage,
    ChangedFile[] ReportedEdits,
    JsonElement? Raw,
    bool IsError);
