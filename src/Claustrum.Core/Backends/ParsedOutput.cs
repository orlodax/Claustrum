using System.Text.Json;
using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

// ReportedEdits is a hint only; WorktreeSnapshot's git diff is truth (docs/PLAN.md A2).
// #126: ErrorKind/ErrorDetail are the backend's own account of a provider refusal (null when it gave none or the run succeeded), and
// ToolCallsCompleted the tool calls that ran to completion (null when the backend cannot count: then the run is never "before any work",
// ProviderErrors.BeforeAnyWork).
public sealed record ParsedOutput(
    string FinalMessage,
    string? SessionId,
    decimal? CostUsd,
    Usage? Usage,
    ChangedFile[] ReportedEdits,
    JsonElement? Raw,
    bool IsError,
    ErrorKind? ErrorKind = null,
    string? ErrorDetail = null,
    int? ToolCallsCompleted = null);
