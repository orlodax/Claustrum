using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// Everything `coordinate` decides — and everything it can fail on — before a job exists: the task
// source, the cast (loaded *and* resolved once, here), the `gh` round trip, the architect's user
// prompt. CoordinateEngine.PlanAsync produces one; Prepare() adds config, role render and model
// resolution on top of it, still with nothing minted (issue #23), so a mistyped cast, a gh failure
// or a malformed claustrum.json all leave no `pending (no result.json)` directory behind (NOTES.md
// "coordinate: a spawned architect is a job run with the cast injected and the tree handed down").
public sealed record CoordinatePlan(
    CoordinateRequest Request,
    Cast Cast,
    string CastName,
    string UserPrompt,
    string Tier,
    ConfigOverrides Overrides,
    CastBudget? CastBudget)
{
    /// <summary>
    /// The architect's delegation, complete except for the job id — the one thing that cannot exist
    /// yet: it is the budget tree every child the architect spawns joins (docs/PLAN.md §D3), it names
    /// the work branch, and the appendix prints it. Both places it appears carry
    /// <see cref="DelegateRequest.JobIdToken"/> until <see cref="PreparedDelegation.ForJob"/> fills
    /// them in. No disk and no issue tracker here: the cast was read and resolved by `PlanAsync`.
    /// </summary>
    public DelegateRequest ToDelegateRequest() => new(
        Role: Cast.ArchitectRole,
        Brief: UserPrompt,
        Cwd: Request.Cwd,
        Tier: Tier,
        Overrides: Overrides,
        ResumeSession: null,
        AttachFiles: [],
        Env: TreeEnv(),
        Stream: Request.Stream,
        DiffCapBytes: Request.DiffCapBytes,
        CastBudget: CastBudget,
        // The architect is one run, never fanned out: max_parallel belongs to the builder it
        // delegates to, and an isolated worktree here would hide the work from the caller's cwd.
        MaxParallel: null,
        CastName: Cast.Name,
        OnStreamLine: Request.OnStreamLine,
        // Request.Overrides, not the resolved ones: only a real --model is "this run", while the
        // resolved value is the cast's own model on every invocation that did not override it.
        SystemAppendix: CoordinationBrief.RenderSystemAppendix(Cast, CastName, Request.Cwd, Request.Overrides.Model));

    /// <summary>
    /// Config, role render, model alias and permission resolved on top of the plan — the rest of what
    /// can be refused with no job directory on disk (issue #23). Both front doors call this *before*
    /// <see cref="JobDirectory.Create"/>, and pass the result to <see cref="CoordinateEngine.RunAsync"/>.
    /// </summary>
    public PreparedDelegation Prepare() => DelegateEngine.Prepare(ToDelegateRequest());

    // What makes every `claustrum run` the architect issues a member of this job's budget tree: the
    // backend inherits these, and a member never forwards them onward (EnvAllowList's ⚠ — caller env
    // wins over the allow-list, which is why they are set here and not on the allow-list).
    // ⚠ CLAUSTRUM_HOME travels with the tree id or `jobs budget <id>` reads a different ledger than
    // the children write to. The coordinate process itself is deliberately NOT given
    // CLAUSTRUM_PARENT_JOB: a member reserves the tree's cap for its whole life, leaving its own
    // children $0 (NOTES.md "coordinate: a spawned architect…").
    private static Dictionary<string, string> TreeEnv()
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal) { [BudgetLedger.TreeVariable] = DelegateRequest.JobIdToken };
        if (AppServices.Platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home)
            env["CLAUSTRUM_HOME"] = home;

        return env;
    }
}
