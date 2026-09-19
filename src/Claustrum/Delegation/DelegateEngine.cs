using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Config;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;

namespace Claustrum.Delegation;

// The pipeline docs/PLAN.md §A5 describes for `run`, now shared by CLI `run` and MCP `delegate`/
// `delegate_async` (extracted from RunCommand once MCP needed the same steps): load config -> the
// harness the role's tier model resolves to (--backend still wins) -> RoleRenderer.Render -> Config.
// Resolve -> RunRequest -> Runner.RunAsync.
public static class DelegateEngine
{
    public const int DefaultTimeoutSeconds = 1800;

    public static async Task<RunResult> RunAsync(DelegateRequest request, CancellationToken cancellationToken) =>
        await RunAsync(request, job: null, cancellationToken);

    // JobManager (MCP delegate_async) needs the job id before the run finishes, so it pre-creates the
    // JobPaths and passes it in; the CLI's synchronous `run` (and MCP's synchronous `delegate`) use
    // the overload above, which lets Runner create one internally.
    public static async Task<RunResult> RunAsync(DelegateRequest request, JobPaths? job, CancellationToken cancellationToken)
    {
        // Config first, then the harness the role's tier model resolves to, then Render — Render
        // must already know the harness it will run on (M1 review finding #2), not a placeholder
        // rendered against regardless of the real target.
        Config config = Config.Load(AppServices.Platform, request.Cwd);
        string tierModelClass = AppServices.RoleRenderer.TierModelClass(request.Role, request.Tier, request.Cwd);
        string harness = config.ResolveBackend(request.Role, tierModelClass, request.Overrides);

        RenderedRole rendered = AppServices.RoleRenderer.Render(request.Role, request.Tier, harness, request.Cwd);
        ResolvedRole resolved = config.Resolve(rendered, request.Overrides);

        decimal? budgetUsd = CastResolution.ApplyBudget(request.Overrides.BudgetUsd, request.CastBudget, config.Merged.Defaults?.BudgetUsd);
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
            OnStreamLine: request.OnStreamLine);

        // max_parallel > 1 (docs/PLAN.md §D4): the job runs isolated in its own git worktree/branch
        // instead of directly in request.Cwd, gated by a cross-process cap so at most maxParallel
        // builders for this role run at once, however many separate `claustrum run` processes a
        // spawned architect fans them out as. Config/tier/harness resolution above still reads
        // request.Cwd — only the backend's own working directory moves.
        if (request.MaxParallel is { } maxParallel && maxParallel > 1)
        {
            job ??= JobDirectory.Create(AppServices.Platform);
            string gateKey = RoleConcurrencyGate.KeyFor(request.CastName, request.Role);
            await using RoleConcurrencyGate gate = await RoleConcurrencyGate.AcquireAsync(
                request.Cwd, gateKey, maxParallel, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            JobWorktreeInfo worktree = await JobWorktree.AddAsync(request.Cwd, job.Id, cancellationToken);

            try
            {
                RunRequest isolatedRunRequest = BuildRunRequest(request, worktree.Path, requestPermission, budgetUsd, timeoutSeconds);
                RunResult isolatedResult = await AppServices.Runner.RunAsync(isolatedRunRequest, resolved, options, job, cancellationToken);
                return isolatedResult with { Worktree = worktree.Path, Branch = worktree.Branch };
            }
            catch (Exception ex)
            {
                // Runner writes a result.json for everything that fails once the backend process has
                // run, so landing here means the run never produced one — a rejected blind gate, a
                // non-positive --timeout, a cancel before the spawn. `jobs clean` only removes
                // worktrees whose job wrote a result.json, so a worktree left behind here could never
                // be cleaned again and its branch would accumulate forever (review finding). Undo it
                // on the way out; the original failure is still what the caller gets.
                if (await JobWorktree.TryRemoveAbandonedAsync(request.Cwd, job.Id, CancellationToken.None) is { } cleanupFailure)
                    throw new AggregateException($"{ex.Message} (and cleaning up {worktree.Path} failed)", ex, cleanupFailure);
                throw;
            }
        }

        RunRequest runRequest = BuildRunRequest(request, request.Cwd, requestPermission, budgetUsd, timeoutSeconds);
        return job is null
            ? await AppServices.Runner.RunAsync(runRequest, resolved, options, cancellationToken)
            : await AppServices.Runner.RunAsync(runRequest, resolved, options, job, cancellationToken);
    }

    private static RunRequest BuildRunRequest(DelegateRequest request, string cwd, PermissionPolicy? permission, decimal? budgetUsd, int timeoutSeconds) => new(
        Role: request.Role,
        Brief: request.Brief,
        BriefFile: null,
        Cwd: cwd,
        Backend: request.Overrides.Backend,
        Model: request.Overrides.Model,
        Effort: request.Overrides.Effort,
        Permission: permission,
        BudgetUsd: budgetUsd,
        Timeout: TimeSpan.FromSeconds(timeoutSeconds),
        ResumeSession: request.ResumeSession,
        AttachFiles: request.AttachFiles,
        Env: request.Env,
        Stream: request.Stream);

    // The CLI's --permission option already validates against the known set (AcceptOnlyFromAmong)
    // before this ever runs; an MCP caller sending an invalid string is exactly the case this should
    // surface as a normal ConfigException-style failure rather than an unreachable-code throw, so it
    // is not marked unreachable the way RunCommand's own copy was.
    private static PermissionLevel RequirePermissionLevel(string value) =>
        PermissionLevelParser.TryParse(value, out PermissionLevel level)
            ? level
            : throw new ConfigException($"unknown permission '{value}'");
}
