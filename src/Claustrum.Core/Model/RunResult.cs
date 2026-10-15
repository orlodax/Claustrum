using System.Text.Json;
using Claustrum.Core.Live;

namespace Claustrum.Core.Model;

// Additive over docs/PLAN.md A2's original sketch, each a new optional field, so schema_version
// stays "1": ReportStatus + Warnings (report extraction, B3), Worktree + Branch (§D4), Commit (#61).
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
    // Set for an isolated run (docs/PLAN.md §D4: max_parallel > 1, or --branch, #63): it ran inside its
    // own `.claustrum/worktrees/<job>` on branch Branch rather than directly in the request's cwd.
    string? Worktree = null,
    string? Branch = null,
    // An isolated run's branch tip after the run, when the run moved it: the runner's commit of the
    // leftovers, or the role's own (#61). Null when nothing changed or the commit failed (a warning).
    string? Commit = null,
    // A live job only (#124): its conversation.jsonl, kept for jobs that end without a report as well.
    ConversationSummary? Conversation = null,
    // A `needs_input` run only (#124 stage 3b): what its report asked, for `jobs answer` and the MCP front door to relay.
    ReportQuestion[]? Questions = null,
    // #126, `failed` runs only. ErrorKind: why, when the backend's output says (provider_quota, provider_auth, model_not_found,
    // provider_transport, or other), with ErrorDetail the provider's own words. BeforeAnyWork: true when the run changed nothing and
    // completed no tool call (ProviderErrors.BeforeAnyWork), null when the backend could not tell.
    ErrorKind? ErrorKind = null,
    string? ErrorDetail = null,
    bool? BeforeAnyWork = null,
    // A cast's fallback (#126): the job this one is the retry of, and the retry that stands in for this one (its receipt is the one to read).
    string? FallbackOf = null,
    string? RetriedAs = null);
