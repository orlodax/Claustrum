using Claustrum.Casts;
using Claustrum.Core;
using Claustrum.Core.Config;
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

        RunRequest runRequest = new(
            Role: request.Role,
            Brief: request.Brief,
            BriefFile: null,
            Cwd: request.Cwd,
            Backend: request.Overrides.Backend,
            Model: request.Overrides.Model,
            Effort: request.Overrides.Effort,
            Permission: requestPermission,
            BudgetUsd: budgetUsd,
            Timeout: TimeSpan.FromSeconds(timeoutSeconds),
            ResumeSession: request.ResumeSession,
            AttachFiles: request.AttachFiles,
            Env: request.Env,
            Stream: request.Stream);

        BackendConfig? backendConfig = null;
        config.Merged.Backends?.TryGetValue(resolved.Backend, out backendConfig);
        RunOptions options = new(
            DiffByteCapBytes: request.DiffCapBytes,
            BackendConfig: backendConfig,
            EnvPassthroughAll: config.Merged.Defaults?.EnvPassthrough == "all",
            OnStreamLine: request.OnStreamLine);

        return job is null
            ? await AppServices.Runner.RunAsync(runRequest, resolved, options, cancellationToken)
            : await AppServices.Runner.RunAsync(runRequest, resolved, options, job, cancellationToken);
    }

    // The CLI's --permission option already validates against the known set (AcceptOnlyFromAmong)
    // before this ever runs; an MCP caller sending an invalid string is exactly the case this should
    // surface as a normal ConfigException-style failure rather than an unreachable-code throw, so it
    // is not marked unreachable the way RunCommand's own copy was.
    private static PermissionLevel RequirePermissionLevel(string value) =>
        PermissionLevelParser.TryParse(value, out PermissionLevel level)
            ? level
            : throw new ConfigException($"unknown permission '{value}'");
}
