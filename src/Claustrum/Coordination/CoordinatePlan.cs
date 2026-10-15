using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;

namespace Claustrum.Coordination;

// Everything `coordinate` decides — and everything it can fail on — before a job exists: the task
// source, the cast (loaded *and* resolved once, here), the architect's worktree and what it needs
// committed (#74), the `gh` round trip, the architect's user prompt. CoordinateEngine.PlanAsync
// produces one; Prepare() adds config, role render and model resolution on top of it, still with
// nothing minted (issue #23), so a mistyped cast, a gh failure or a malformed claustrum.json all leave
// no `pending (no result.json)` directory behind (NOTES.md "coordinate: a spawned architect is a job
// run with the cast injected and the tree handed down").
public sealed record CoordinatePlan(
    CoordinateRequest Request,
    Cast Cast,
    string CastName,
    string UserPrompt,
    string Tier,
    ConfigOverrides Overrides,
    CastBudget? CastBudget,
    // #74: the cwd is in a git repository, so the architect runs in its own worktree — decided once,
    // here, so the request, the appendix and the user prompt cannot disagree about it.
    bool Isolated,
    // Cast-resolution warnings (built-in fallback, stored→current drift) that reach the job's
    // warnings[] and stderr, just as they do for `run` (#122 item 6).
    string[] CastWarnings,
    // #74 G1: what that worktree will lack and does not refuse (a harness config's local edit); each door
    // hands them to its caller before the run — not on the RunResult, which is the architect's receipt.
    string[] Warnings,
    // #126: the cast's fallback for a spawned architect (null when a --model flag chose it), and what it resolved to at creation.
    string? Fallback = null,
    string? FallbackResolved = null)
{
    /// <summary>
    /// The architect's delegation, complete except for the job id — the one thing that cannot exist
    /// yet: it is the budget tree every child the architect spawns joins (docs/PLAN.md §D3), it names
    /// the work branch and — isolated — the worktree, and the appendix prints it. Every place it appears
    /// carries <see cref="DelegateRequest.JobIdToken"/> until <see cref="PreparedDelegation.ForJob"/>
    /// (system prompt, env) and <see cref="BindUserPrompt"/> (the user prompt) fill it in. No disk and
    /// no issue tracker here: the cast was read and resolved by `PlanAsync`.
    /// </summary>
    public DelegateRequest ToDelegateRequest() => new(
        Role: Cast.ArchitectRole,
        Brief: UserPrompt,
        Cwd: Request.Cwd,
        Tier: Tier,
        Overrides: Overrides,
        ResumeSession: Request.Resume?.SessionId,
        AttachFiles: [],
        Env: TreeEnv(),
        Stream: Request.Stream,
        StreamRaw: Request.StreamRaw,
        DiffCapBytes: Request.DiffCapBytes,
        CastBudget: CastBudget,
        // The architect is one run, never fanned out: max_parallel belongs to the builder it delegates
        // to. Its own worktree (#74) is Isolate, which takes no slot.
        MaxParallel: null,
        CastName: Cast.Name,
        OnStreamLine: Request.OnStreamLine,
        // Request.Overrides, not the resolved ones: only a real --model is "this run", while the
        // resolved value is the cast's own model on every invocation that did not override it.
        SystemAppendix: CoordinationBrief.RenderSystemAppendix(Cast, CastName, Request.Cwd, Request.Overrides.Model, Isolated, Request.Resume?.TreeId, Request.Resume?.WorktreeOf),
        Isolate: Isolated,
        PreRunWarnings: CastWarnings,
        // #122: the cast is per-developer and git-ignored — if it's not tracked in HEAD, the
        // worktree cut from HEAD won't have it. Pass it so RunIsolatedAsync copies it in, under the
        // file name the children are told (`--cast <CastName>`), not the document's own `name`.
        WorktreeCastCopy: Isolated ? Cast with { Name = CastName } : null,
        Live: Request.Live,
        ReplyTimeoutSeconds: Request.ReplyTimeoutSeconds,
        // #124 stage 3b: a resumed architect continues its session on the branch it was on (checked out in a new worktree, so it stays
        // isolated), and records what a further resume of it needs.
        Branch: Isolated ? Request.Resume?.Branch : null,
        ParentJobId: Request.Resume?.JobId,
        Coordinate: new CoordinateOrigin(CastName, Request.TierFlag, Request.Overrides, Request.Resume?.TreeId),
        WorktreeOf: Isolated ? Request.Resume?.WorktreeOf : null,
        Pin: Request.Resume?.Pin,
        Fallback: Fallback,
        FallbackResolved: FallbackResolved);

    /// <summary>
    /// Config, role render, model alias and permission resolved on top of the plan — the rest of what
    /// can be refused with no job directory on disk (issue #23). Both front doors call this *before*
    /// <see cref="JobDirectory.Create"/>, and pass the result to <see cref="CoordinateEngine.RunAsync"/>.
    /// </summary>
    public PreparedDelegation Prepare() => PrepareRequest(ToDelegateRequest());

    /// <summary>
    /// <see cref="Prepare"/> for a request derived from this plan's: the architect's retry on its cast's fallback (#126), whose backend
    /// may not hold a live session even though the first one did, and whose children must be told so.
    /// </summary>
    public static PreparedDelegation PrepareRequest(DelegateRequest request)
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(request);

        // #124 stage 2 review F2: whether the architect holds a live session is known only now (the backend has to have a live protocol and
        // the role a report), and it is what lets a child route its question to it. Handed down next to the tree id; a child never infers it.
        bool architectLive = prepared.Options.Live && prepared.Role.HasReport;
        Dictionary<string, string> env = new(prepared.Request.Env, StringComparer.Ordinal) { [BudgetLedger.ArchitectLiveVariable] = architectLive ? "1" : "0" };
        return prepared with { Request = prepared.Request with { Env = env } };
    }

    /// <summary>
    /// <paramref name="prepared"/> with its user prompt's `Working directory:` line naming the real
    /// worktree of job <paramref name="jobId"/> (#74). That one line in the `## Context` block, not the whole
    /// brief: <see cref="PreparedDelegation.ForJob"/> fills the system prompt and the env, but the task above
    /// is the caller's or an issue's text, and may quote <see cref="DelegateRequest.JobIdToken"/> or the line itself (#89).
    /// </summary>
    public PreparedDelegation BindUserPrompt(PreparedDelegation prepared, string jobId)
    {
        if (!Isolated)
            return prepared;

        string brief = CoordinationBrief.BindWorkingDirectory(
            prepared.Request.Brief,
            CoordinationBrief.ArchitectCwd(Request.Cwd, isolated: true, Request.Resume?.WorktreeOf),
            JobWorktree.PathFor(Request.Cwd, Request.Resume?.WorktreeOf ?? jobId));

        return prepared with { Request = prepared.Request with { Brief = brief } };
    }

    /// <summary>
    /// The budget tree the architect's children join: this job's own id, or — resumed — the tree of the job it continues, so the cap and the
    /// ledger carry on. Null for a fresh plan, whose tree is its own job id, known once the job is minted.
    /// </summary>
    public string? ContinuedTree => Request.Resume?.TreeId;

    // What makes every `claustrum run` the architect issues a member of this job's budget tree: the
    // backend inherits these, and a member never forwards them onward (EnvAllowList's ⚠ — caller env
    // wins over the allow-list, which is why they are set here and not on the allow-list). Prepare adds
    // the architect's liveness (CLAUSTRUM_ARCHITECT_LIVE) once the backend is resolved.
    // ⚠ CLAUSTRUM_HOME travels with the tree id or `jobs budget <id>` reads a different ledger than
    // the children write to. The coordinate process itself is deliberately NOT given
    // CLAUSTRUM_PARENT_JOB: a member reserves the tree's cap for its whole life, leaving its own
    // children $0 (NOTES.md "coordinate: a spawned architect…").
    private Dictionary<string, string> TreeEnv()
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal) { [BudgetLedger.TreeVariable] = ContinuedTree ?? DelegateRequest.JobIdToken };
        if (AppServices.Platform.GetEnvironmentVariable("CLAUSTRUM_HOME") is { Length: > 0 } home)
            env["CLAUSTRUM_HOME"] = home;

        return env;
    }
}
