using System.Text.Json;

namespace Claustrum.Core.Model;

// Additive over docs/PLAN.md A2's original sketch: ReportStatus + Warnings were added once report
// extraction (B3) was implemented — schema_version stays "1" because both are new optional fields.
public sealed record RunResult(
    string SchemaVersion,
    string JobId,
    RunStatus Status,
    string Backend,
    string Model,
    string Role,
    string FinalMessage,
    ChangedFile[] ChangedFiles,
    string? Diff,
    bool DiffTruncated,
    string? SessionId,
    decimal? CostUsd,
    Usage? Usage,
    int ExitCode,
    string LogPath,
    double DurationSeconds,
    string? Error,
    JsonElement? Raw,
    ClaustrumReport? Report,
    ReportStatus ReportStatus,
    string[] Warnings,
    // Set only for a max_parallel > 1 job (docs/PLAN.md §D4): the job ran inside its own
    // `.claustrum/worktrees/<job>` on branch Branch rather than directly in the request's cwd.
    // Additive like ReportStatus/Warnings above — schema_version stays "1".
    string? Worktree = null,
    string? Branch = null);
