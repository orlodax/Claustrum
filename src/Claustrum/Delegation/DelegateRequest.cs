using System.Text.Json.Serialization;
using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Delegation;

// Harness-neutral input to DelegateEngine.Prepare/RunAsync: everything CLI `run` and MCP `delegate`
// need to agree on once each front door has finished its own arg parsing (a brief already read from
// --brief-file/stdin on the CLI side, "K=V" pairs already split, etc. — see docs/PLAN.md A1 "one
// executable; MCP and CLI are thin adapters over Core", extended one layer up now that both share
// this pipeline instead of duplicating it). A detached job (#124) carries it to its host as host-request.json, so
// the two things a file cannot hold (a callback, a cast `coordinate` copies) are not serialized.
public sealed record DelegateRequest(
    string Role,
    string Brief,
    string Cwd,
    string Tier,
    ConfigOverrides Overrides,
    string? ResumeSession,
    string[] AttachFiles,
    Dictionary<string, string> Env,
    bool Stream,
    int DiffCapBytes,
    CastBudget? CastBudget = null,
    int? MaxParallel = null,
    // The cast MaxParallel came from (CastApplication.Resolve). Only RoleConcurrencyGate reads it:
    // its slot pool has to be per cast, not per role (docs/PLAN.md §D4).
    string? CastName = null,
    [property: JsonIgnore] Action<string>? OnStreamLine = null,
    // Appended to the rendered role's system body before Config.Resolve — how `coordinate` gets the
    // cast into its architect's prompt without Roles/Core learning what a cast is (NOTES.md
    // "coordinate: a spawned architect is a job run with the cast injected…"). May carry JobIdToken.
    string? SystemAppendix = null,
    // `run --branch` / MCP `branch` (#63): an existing local branch the run continues on, isolated in a
    // new worktree whatever MaxParallel says — how a reviewed builder's branch gets its fix.
    string? Branch = null,
    // #74: isolated in a new `claustrum/<job id>` worktree cut from HEAD even with no MaxParallel,
    // which alone still decides a cap — `coordinate`'s architect, which takes no slot.
    bool Isolate = false,
    // Set by the MCP tools: their `files` are read as regular files only (RunRequest.AttachmentsMustBeRegular).
    bool AttachmentsMustBeRegular = false,
    // #121: cast-resolution warnings (built-in fallback, stored Δ) that DelegateEngine.Prepare
    // folds into RunOptions.PreRunWarnings and from there into result.json's warnings[] and stderr.
    string[]? PreRunWarnings = null,
    // `--stream-raw` on the CLI: echo unformatted raw lines to stderr (issue #123).
    bool StreamRaw = false,
    // #122: `coordinate`'s cast, set when the cast was not tracked in HEAD — RunIsolatedAsync copies
    // it into the worktree after creation so the architect can load it. Only set by CoordinatePlan;
    // null for all other paths (CLI `run`, MCP `delegate`).
    [property: JsonIgnore] Cast? WorktreeCastCopy = null,
    // #124: set by the front doors (CLI TTY rule, MCP delegate vs delegate_async); copied into RunOptions by Prepare.
    bool Live = false,
    int ReplyTimeoutSeconds = 0,
    // #124 stage 3b: the `needs_input` job this one continues (`jobs answer`, `coordinate --resume`); copied into request.json.
    string? ParentJobId = null,
    // Set only on `coordinate`'s architect: what a resume of it needs beyond this request (CoordinateOrigin).
    CoordinateOrigin? Coordinate = null)
{
    /// <summary>
    /// The exact token <see cref="SystemAppendix"/> and <see cref="Env"/> values may carry in place of
    /// a job id that does not exist yet: config and role are resolved before the directory is minted
    /// (issue #23), and `coordinate` is the only caller that needs the id inside text it renders —
    /// the id is the budget tree its children join, and it names the work branch. Substituted by
    /// <see cref="PreparedDelegation.ForJob"/>, which only runs when a caller hands in a JobPaths.
    /// </summary>
    public const string JobIdToken = "{{job_id}}";
}
