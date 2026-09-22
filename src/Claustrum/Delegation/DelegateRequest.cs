using Claustrum.Casts;
using Claustrum.Core.Config;

namespace Claustrum.Delegation;

// Harness-neutral input to DelegateEngine.Prepare/RunAsync: everything CLI `run` and MCP `delegate`
// need to agree on once each front door has finished its own arg parsing (a brief already read from
// --brief-file/stdin on the CLI side, "K=V" pairs already split, etc. — see docs/PLAN.md A1 "one
// executable; MCP and CLI are thin adapters over Core", extended one layer up now that both share
// this pipeline instead of duplicating it).
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
    Action<string>? OnStreamLine = null,
    // Appended to the rendered role's system body before Config.Resolve — how `coordinate` gets the
    // cast into its architect's prompt without Roles/Core learning what a cast is (NOTES.md
    // "coordinate: a spawned architect is a job run with the cast injected…"). May carry JobIdToken.
    string? SystemAppendix = null)
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
