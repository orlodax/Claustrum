using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// Everything `coordinate` decides — and everything it can fail on — before a job exists: the task
// source, the cast, the `gh` round trip, the architect's user prompt. CoordinateEngine.PlanAsync
// produces one; ToDelegateRequest finishes the request once an id has been minted, so a mistyped
// cast or a gh failure leaves no `pending (no result.json)` directory behind (NOTES.md "coordinate:
// a spawned architect is a job run with the cast injected and the tree handed down").
public sealed record CoordinatePlan(CoordinateRequest Request, Cast Cast, string CastName, string UserPrompt)
{
    /// <summary>
    /// The plan bound to the job that will run it. The job id is the only thing still missing: it is
    /// the budget tree every child the architect spawns joins (docs/PLAN.md §D3), and the appendix
    /// prints it. No network and no issue tracker here — only the cast file, re-read for the
    /// architect's tier/model precedence, which <see cref="CoordinateEngine.PlanAsync"/> has already
    /// loaded once and refused a missing one on.
    /// </summary>
    public DelegateRequest ToDelegateRequest(JobPaths job)
    {
        (string tier, ConfigOverrides overrides, CastBudget? castBudget, _, string? resolvedCastName) =
            CastApplication.Resolve(Request.Cwd, Cast.ArchitectRole, Request.CastName, Request.TierFlag, Request.Overrides);

        return new DelegateRequest(
            Role: Cast.ArchitectRole,
            Brief: UserPrompt,
            Cwd: Request.Cwd,
            Tier: tier,
            Overrides: overrides,
            ResumeSession: null,
            AttachFiles: [],
            Env: TreeEnv(job),
            Stream: Request.Stream,
            DiffCapBytes: Request.DiffCapBytes,
            CastBudget: castBudget,
            // The architect is one run, never fanned out: max_parallel belongs to the builder it
            // delegates to, and an isolated worktree here would hide the work from the caller's cwd.
            MaxParallel: null,
            CastName: resolvedCastName,
            OnStreamLine: Request.OnStreamLine,
            // Request.Overrides, not the resolved ones: only a real --model is "this run", while the
            // resolved value is the cast's own model on every invocation that did not override it.
            SystemAppendix: CoordinationBrief.RenderSystemAppendix(Cast, CastName, Request.Cwd, job.Id, Request.Overrides.Model));
    }

    // What makes every `claustrum run` the architect issues a member of this job's budget tree: the
    // backend inherits these, and a member never forwards them onward (EnvAllowList's ⚠ — caller env
    // wins over the allow-list, which is why they are set here and not on the allow-list).
    // ⚠ CLAUSTRUM_HOME travels with the tree id or `jobs budget <id>` reads a different ledger than
    // the children write to. The coordinate process itself is deliberately NOT given
    // CLAUSTRUM_PARENT_JOB: a member reserves the tree's cap for its whole life, leaving its own
    // children $0 (NOTES.md "coordinate: a spawned architect…").
    private static Dictionary<string, string> TreeEnv(JobPaths job)
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal) { [BudgetLedger.TreeVariable] = job.Id };
        if (AppServices.Platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home)
            env["CLAUSTRUM_HOME"] = home;

        return env;
    }
}
