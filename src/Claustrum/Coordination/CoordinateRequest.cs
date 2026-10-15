using Claustrum.Core.Config;
using Claustrum.Delegation;

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
    Action<string>? OnStreamLine = null,
    // `--stream-raw` on the CLI: echo unformatted raw lines (issue #123).
    bool StreamRaw = false,
    // #124: the architect's live options, from `--no-live`/`--reply-timeout` and the TTY rule (CLI only for now).
    bool Live = false,
    int ReplyTimeoutSeconds = 0,
    // `coordinate --resume` / MCP job_answer (#124 stage 3b): the architect of a finished `needs_input` coordinate job, continued.
    CoordinateResume? Resume = null);

/// <summary>
/// The coordinate job being continued and what the continuation reuses: its budget tree (<see cref="TreeId"/>, the first job of the
/// chain, so the cap and the ledger carry on), its architect's backend session and work branch (null when it ran in place), and the
/// human's answers as the next message. <see cref="WorktreeOf"/> is the job whose worktree directory the architect ran in, which it takes over
/// again (F1), and <see cref="Pin"/> what it ran as (F5).
/// </summary>
public sealed record CoordinateResume(string JobId, string TreeId, string SessionId, string? Branch, string Answers, string? WorktreeOf = null, ResumePin? Pin = null);
