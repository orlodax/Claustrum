using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Roles;

namespace Claustrum.Delegation;

// The pipeline docs/PLAN.md §A5 describes for `run`, now shared by CLI `run` and MCP `delegate`/
// `delegate_async` (extracted from RunCommand once MCP needed the same steps): load config -> the
// harness the role's tier model resolves to (--backend still wins) -> RoleRenderer.Render -> Config.
// Resolve -> RunRequest -> Runner.RunAsync. Cut in two at the job directory (issue #23): Prepare is
// everything decidable without one, RunAsync everything that needs one.
public static class DelegateEngine
{
    public const int DefaultTimeoutSeconds = 1800;

    // The whole pipeline for a caller that has no job directory to lose: `run` and `delegate`, where
    // Runner mints one itself after the blind gate. ⚠ There is deliberately no
    // `RunAsync(DelegateRequest, JobPaths, …)` beside it — a caller that mints first must Prepare
    // first, or it is back to resolving config and role over a directory nothing will ever close
    // (issue #23). JobManager and `coordinate` take the PreparedDelegation overload for that reason.
    public static async Task<RunResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken) =>
        await RunAsync(Prepare(request), job: null, cancellationToken);

    /// <summary>
    /// Everything this pipeline can decide — and refuse — before a job directory exists (issue #23):
    /// a malformed `claustrum.json`, a tier the role has no model class for, an unresolvable model
    /// alias, a role resolved to a harness its role.json does not list and an unknown permission all
    /// throw here, where the caller that would have minted the directory has not yet. Nothing on disk
    /// is written and no backend binary is looked up — only the registry's names are consulted.
    /// </summary>
    public static PreparedDelegation Prepare(DelegateRequest request)
    {
        // Config first, then the harness the role's tier model resolves to, then Render — Render
        // must already know the harness it will run on (M1 review finding #2), not a placeholder
        // rendered against regardless of the real target.
        Config config = Config.Load(AppServices.Platform, request.Cwd);
        string tierModelClass = AppServices.RoleRenderer.TierModelClass(request.Role, request.Tier, request.Cwd);
        string harness = config.ResolveBackend(request.Role, tierModelClass, request.Overrides);
        RequireSupportedHarness(request.Role, harness, request.Cwd);

        RenderedRole rendered = AppServices.RoleRenderer.Render(request.Role, request.Tier, harness, request.Cwd);

        // Call-site data appended after the renderer, never a template token: `coordinate` puts the
        // whole cast here (NOTES.md "A cast is call-site data, not a Core concept"), and it lands in
        // the job's system.md for free because Runner writes what Config.Resolve produced.
        if (request.SystemAppendix?.Trim() is { Length: > 0 } appendix)
            rendered = rendered with { SystemBody = InsertAppendix(rendered.SystemBody, appendix) };

        ResolvedRole resolved = config.Resolve(rendered, request.Overrides);

        // docs/PLAN.md §D4: inside a job tree (CLAUSTRUM_PARENT_JOB, §D2) the cast's budget_usd is the
        // whole tree's, so it goes to the ledger instead of straight to this one run — Runner clamps
        // the child to what is left, which is why the per-run cap it starts from is only an explicit
        // --budget. A cast that says unlimited yields no tree budget to share, and no tree at all
        // leaves the §D1 precedence exactly as it was (NOTES.md "Tree budget accounting is a file
        // ledger").
        decimal? treeBudget = CastResolution.ApplyBudget(flagBudget: null, request.CastBudget, config.Merged.Defaults?.BudgetUsd);
        JobTreeBudget? tree = BudgetLedger.TreeIdFor(AppServices.Platform) is { } treeId && treeBudget is { } shared
            ? new JobTreeBudget(treeId, shared, Math.Max(1, request.MaxParallel ?? 1))
            : null;
        decimal? budgetUsd = tree is null
            ? CastResolution.ApplyBudget(request.Overrides.BudgetUsd, request.CastBudget, config.Merged.Defaults?.BudgetUsd)
            : request.Overrides.BudgetUsd;

        int timeoutSeconds = request.Overrides.TimeoutSeconds ?? config.Merged.Defaults?.TimeoutSeconds ?? DefaultTimeoutSeconds;
        PermissionPolicy? requestPermission = request.Overrides.Permission is { } permissionValue
            ? new PermissionPolicy(RequirePermissionLevel(permissionValue), request.Overrides.Deny ?? [])
            : null;

        BackendConfig? backendConfig = null;
        config.Merged.Backends?.TryGetValue(resolved.Backend, out backendConfig);
        RunOptions options = new(
            DiffByteCapBytes: request.DiffCapBytes,
            BackendConfig: backendConfig,
            EnvPassthroughAll: config.Merged.Defaults?.EnvPassthrough == "all",
            OnStreamLine: request.OnStreamLine,
            Tree: tree);

        return new PreparedDelegation(request, resolved, options, budgetUsd, timeoutSeconds, requestPermission);
    }

    /// <summary>
    /// The half that needs a job: the concurrency gate a cast role with a numeric max_parallel pays for
    /// (#58: at every value, 1 included), worktree isolation from 2 or with --branch (#63), then Runner.
    /// A <paramref name="job"/> of null lets Runner mint one after its own blind gate (the CLI `run`
    /// ordering); anything a caller minted itself is handed straight on, and its id fills
    /// <see cref="DelegateRequest.JobIdToken"/> here, at the last moment.
    /// </summary>
    public static async Task<RunResult> RunAsync(PreparedDelegation prepared, JobPaths? job, CancellationToken cancellationToken)
    {
        DelegateRequest request = prepared.Request;
        if (request.Branch is null && request.MaxParallel is null or < 1)
            return await RunInPlaceAsync(prepared, job, cancellationToken);

        // F7: both paths below mint a job, cut a worktree or wait for a slot before Runner's own checks
        // run, so a brief Runner would reject is rejected here first: "a blind-gate rejection leaves no
        // job directory" holds on every path. Runner checks again; that is its last line.
        Runner.Validate(BuildRunRequest(prepared, request.Cwd), prepared.Role, AppServices.Platform);

        if (request.Branch is not null || request.MaxParallel is not 1)
            return await RunIsolatedAsync(prepared, job ?? JobDirectory.Create(AppServices.Platform), cancellationToken);

        // #58: max_parallel 1 — the one cap that does not isolate — is a cap too, so two runs of one cast
        // role queue instead of editing one tree at once. No pre-admission here: Runner admits itself
        // under the slot held below — only isolation, which cuts a branch before Runner runs, has to
        // admit first. A job is minted for the refusal only, so Runner still mints the CLI's.
        RoleConcurrencyGate slot;
        try
        {
            slot = await AcquireSlotAsync(request, cap: 1, prepared.TimeoutSeconds, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return await Runner.RefuseAsync(job ?? JobDirectory.Create(AppServices.Platform), prepared.Role, RunStatus.Failed, ex.Message);
        }

        await using RoleConcurrencyGate gate = slot;
        return await RunInPlaceAsync(prepared, job, cancellationToken);
    }

    // #62's deny half: an isolated run may not move any checkout's HEAD, natively enforced where the
    // harness has a deny mechanism, on top of the prompt trailer Runner adds. Internal for the tests.
    internal static ResolvedRole IsolatedRole(ResolvedRole role) =>
        role with { Permission = role.Permission with { Deny = [.. role.Permission.Deny.Union(JobWorktree.IsolationDeny, StringComparer.Ordinal)] } };

    private static async Task<RunResult> RunInPlaceAsync(PreparedDelegation prepared, JobPaths? job, CancellationToken cancellationToken)
    {
        PreparedDelegation bound = job is null ? prepared : prepared.ForJob(job.Id);
        RunRequest runRequest = BuildRunRequest(bound, bound.Request.Cwd);

        return job is null
            ? await AppServices.Runner.RunAsync(runRequest, bound.Role, bound.Options, cancellationToken)
            : await AppServices.Runner.RunAsync(runRequest, bound.Role, bound.Options, job, cancellationToken);
    }

    // max_parallel > 1 (docs/PLAN.md §D4) or --branch (#63): the job runs in its own git worktree
    // instead of directly in request.Cwd, behind the cast's cross-process cap when it has a numeric
    // max_parallel, however many separate `claustrum run` processes a spawned architect fans out —
    // and with --branch behind that branch's lock too (R1). Prepare's config/tier/harness resolution
    // still read request.Cwd — only the backend's own working directory moves.
    private static async Task<RunResult> RunIsolatedAsync(PreparedDelegation prepared, JobPaths job, CancellationToken cancellationToken)
    {
        PreparedDelegation bound = prepared.ForJob(job.Id);
        DelegateRequest request = bound.Request;
        ResolvedRole role = IsolatedRole(bound.Role);

        // Before the gate: a branch that does not exist is answered now, not after a wait for a slot.
        if (request.Branch is { } branch && !await JobWorktree.BranchExistsAsync(request.Cwd, branch, cancellationToken))
            return await Runner.RefuseAsync(job, role, RunStatus.Failed, JobWorktree.NoSuchBranch(request.Cwd, branch));

        RoleConcurrencyGate? slot = null;
        if (request.MaxParallel is { } cap && cap >= 1)
        {
            try
            {
                slot = await AcquireSlotAsync(request, cap, bound.TimeoutSeconds, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                // issue #20: every other pre-spawn refusal on this path is a RunResult document, and an
                // architect parsing --json cannot read an exception on stderr. Nothing to undo — no
                // worktree, no branch, and admission comes after the gate, so no reservation exists yet.
                return await Runner.RefuseAsync(job, role, RunStatus.Failed, ex.Message);
            }
        }

        await using RoleConcurrencyGate? gate = slot;

        // R1: one run per branch, from before its worktree is freed and added until its result.json is
        // written: git has no lock between "checked out elsewhere?" and `worktree add` (two parallel adds
        // of one branch succeeded 35/40, git 2.56), and two worktrees on one branch revert each other's
        // commits. Always after the slot, so no run holds what another waits for; before admission.
        RoleConcurrencyGate? branchLock = null;
        if (request.Branch is { } locked)
        {
            try
            {
                branchLock = await AcquireBranchLockAsync(request.Cwd, locked, bound.TimeoutSeconds, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                return await Runner.RefuseAsync(job, role, RunStatus.Failed, $"--branch {locked}: another run on this branch outlasted the wait — {ex.Message}");
            }
        }

        await using RoleConcurrencyGate? branchGate = branchLock;

        // Binding, and before any isolation: this path cuts a branch and holds a slot before Runner
        // ever sees the request, so a job the ledger will refuse has to be refused here. The slot
        // comes first anyway — a reservation must never wait behind the gate, and a sibling that
        // finishes in that wait releases budget this job can then have.
        BudgetAdmission? admission = await TryAdmitAsync(bound.Options.Tree, job.Id, role.Name, bound.BudgetUsd);
        RunOptions isolatedOptions = bound.Options with { Admission = admission };

        if (admission is { Admitted: false })
        {
            // No worktree, no branch, no slot: Runner only writes the refusal's result.json, and
            // the `await using`s above dispose both a second time, which is a no-op.
            if (gate is not null)
                await gate.DisposeAsync();
            if (branchGate is not null)
                await branchGate.DisposeAsync();
            RunRequest refusedRunRequest = BuildRunRequest(bound, request.Cwd);
            return await AppServices.Runner.RunAsync(refusedRunRequest, role, isolatedOptions, job, cancellationToken);
        }

        JobWorktreeInfo? worktree = null;
        try
        {
            worktree = request.Branch is { } existing
                ? await JobWorktree.CheckOutBranchAsync(request.Cwd, job.Id, existing, JobDirectory.ResolveRoot(AppServices.Platform), cancellationToken)
                : await JobWorktree.AddAsync(request.Cwd, job.Id, cancellationToken);

            RunRequest isolatedRunRequest = BuildRunRequest(bound, worktree.Path);
            return await AppServices.Runner.RunAsync(isolatedRunRequest, role, isolatedOptions with { Worktree = worktree }, job, cancellationToken);
        }
        catch (BranchRefusedException ex)
        {
            // A receipt, not a throw (F11), and after admission, so the reservation closes at $0
            // through Runner's funnel instead of waiting to be read as abandoned. No worktree is left.
            return await Runner.RefuseAsync(job, role, RunStatus.Failed, ex.Message, admission?.Reservation);
        }
        catch (Exception ex)
        {
            // Runner writes a result.json for everything that fails once the backend process has run,
            // so landing here means none was written — a failed `-b` add or a cancel before the run (an
            // add undoes what it made itself, T1), an unwritable result.json after it. Two things must
            // be undone. The reservation, because Runner closes it through its funnel but may never have
            // received it; releasing the handle is enough (the next admission reads the entry as
            // abandoned, worth $0) and disposing twice is harmless. And the worktree: `jobs clean` only
            // removes those of finished jobs, so one left here would never go — its branch too, unless
            // the run moved it.
            if (admission?.Reservation is { } reservation)
                await reservation.DisposeAsync();

            if (worktree is not null && await JobWorktree.TryRemoveAbandonedAsync(request.Cwd, worktree, CancellationToken.None) is { } cleanupFailure)
                throw new AggregateException($"{ex.Message} (and cleaning up {worktree.Path} failed)", ex, cleanupFailure);
            throw;
        }
    }

    private static async Task<RoleConcurrencyGate> AcquireSlotAsync(DelegateRequest request, int cap, int timeoutSeconds, CancellationToken cancellationToken) =>
        await RoleConcurrencyGate.AcquireAsync(
            request.Cwd, RoleConcurrencyGate.KeyFor(request.CastName, request.Role), cap, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

    private static async Task<RoleConcurrencyGate> AcquireBranchLockAsync(string cwd, string branch, int timeoutSeconds, CancellationToken cancellationToken) =>
        await RoleConcurrencyGate.AcquireAsync(cwd, JobWorktree.LockKeyFor(branch), 1, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

    // role.json `harnesses` is where a role is written to run (#43): anywhere else it fails to render
    // (demo-author's and ui-reviewer's browser part is claude-only) or runs without the tools it needs,
    // so it is refused here, before Render and before any job exists. Only a backend Claustrum knows is
    // checked: an unknown name is left to Render (a claude-only part fails there) or to Runner's
    // backend_missing, as before. Cast entries arrive as overrides, so the message cannot tell a flag
    // from the cast and names every source.
    private static void RequireSupportedHarness(string role, string harness, string cwd)
    {
        if (!AppServices.Backends.TryGet(harness, out _))
            return;

        string[] supported = AppServices.RoleLibrary.LoadRole(role, cwd).Definition.Harnesses;
        if (!supported.Contains(harness, StringComparer.Ordinal))
        {
            throw new RoleRenderException(
                $"role '{role}' runs on {string.Join(", ", supported)} only (its role.json `harnesses`), but this run resolved it to "
                + $"'{harness}' through --backend/--model, the cast or claustrum.json — point it at one of those there; or, if this "
                + $"repo ships the role's parts for '{harness}', add it to `harnesses` in .claustrum/roles/{role}/role.json");
        }
    }

    // Above `## House rules`, not after it: dead-last in a ~23 KB system prompt is the position
    // RoleRenderer.ComposeSystemBody measured as skippable (its comment carries the tester report),
    // which is why `## Report format` was moved off the end too. A body without that heading — one
    // no RoleRenderer composed — keeps the plain append.
    private static string InsertAppendix(string systemBody, string appendix)
    {
        int houseRules = systemBody.IndexOf("\n## House rules\n", StringComparison.Ordinal);
        return houseRules < 0
            ? $"{systemBody}\n\n{appendix}"
            : $"{systemBody[..houseRules].TrimEnd()}\n\n{appendix}\n{systemBody[houseRules..]}";
    }

    // The isolated path's own admission, under the ledger's lock and binding — the earlier
    // PeekRemainingAsync here was not, and a sibling finishing in the gap between peek and admission
    // let a job run directly in the caller's cwd, outside both the worktree and the concurrency cap
    // (review finding). Null means "no tree at all, or a ledger that could not be read": Runner then
    // admits under the slot this caller is still holding, which turns a ledger error back into its
    // Failed result and keeps a transient one from running this job un-isolated.
    private static async Task<BudgetAdmission?> TryAdmitAsync(JobTreeBudget? tree, string jobId, string role, decimal? requestedCap)
    {
        if (tree is not { } active)
            return null;

        try
        {
            return await BudgetLedger.AdmitAsync(AppServices.Platform, active, jobId, role, requestedCap);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RunRequest BuildRunRequest(PreparedDelegation prepared, string cwd) => new(
        Role: prepared.Request.Role,
        Brief: prepared.Request.Brief,
        BriefFile: null,
        Cwd: cwd,
        Backend: prepared.Request.Overrides.Backend,
        Model: prepared.Request.Overrides.Model,
        Effort: prepared.Request.Overrides.Effort,
        Permission: prepared.Permission,
        BudgetUsd: prepared.BudgetUsd,
        Timeout: TimeSpan.FromSeconds(prepared.TimeoutSeconds),
        ResumeSession: prepared.Request.ResumeSession,
        AttachFiles: prepared.Request.AttachFiles,
        Env: prepared.Request.Env,
        Stream: prepared.Request.Stream);

    // The CLI's --permission option already validates against the known set (AcceptOnlyFromAmong)
    // before this ever runs; an MCP caller sending an invalid string is exactly the case this should
    // surface as a normal ConfigException-style failure rather than an unreachable-code throw, so it
    // is not marked unreachable the way RunCommand's own copy was.
    private static PermissionLevel RequirePermissionLevel(string value) =>
        PermissionLevelParser.TryParse(value, out PermissionLevel level)
            ? level
            : throw new ConfigException($"unknown permission '{value}'");
}
