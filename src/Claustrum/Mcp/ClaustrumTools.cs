using System.ComponentModel;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Mcp.Json;
using Claustrum.Roles.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Claustrum.Mcp;

// docs/PLAN.md §A6 + §D5's `coordinate`: the MCP tools, thin adapters over the same DelegateEngine/
// CoordinateEngine/JobManager/RoleLibrary/BackendRegistry/CastStore the CLI uses — no logic lives
// here that the CLI does not already exercise. Every tool returns a pre-serialized JSON string.
[McpServerToolType]
public sealed class ClaustrumTools
{
    // §A2: 200 KB on the CLI door, 64 KB on MCP.
    private const int McpDiffCapBytes = 64 * 1024;

    [McpServerTool(Name = "delegate")]
    [Description(
        "Delegate a task to a Claustrum role, blocking until it finishes, and return the parsed RunResult " +
        "(status, changed_files, diff, report). Permission levels: 'readonly' (read-only tools only — the " +
        "default for a blind role like code-reviewer), 'edit' (edit, no shell), 'edit+shell' (edit + shell — " +
        "builder/tester's default), 'full' (dangerously skips all permission checks — use only when you mean " +
        "it). A role with blind:true (see list_roles) must be briefed with only the task as stated and how to " +
        "get the diff — never rationale, a plan, or a pasted claustrum-report block; the runner refuses such a " +
        "brief outright (docs/PLAN.md §B3 blind gate).")]
    public static Task<string> DelegateAsync(
        [Description("Role name, e.g. builder, code-reviewer, tester (see list_roles).")] string role,
        [Description("The brief: markdown with H2 sections ## Task/## Scope/## Must still work/## Diff (docs/PLAN.md §B3).")] string brief,
        [Description("Working directory (default: the server's own cwd).")] string? cwd = null,
        string? backend = null,
        string? model = null,
        string? effort = null,
        [Description("Role tier: high (default), xhigh, or max.")] string? tier = null,
        [Description("readonly | edit | edit+shell | full — see the tool description.")] string? permission = null,
        string[]? deny = null,
        decimal? budgetUsd = null,
        [Description("Timeout in seconds (default: unset, so the config layers' defaults.timeout_seconds decides, falling back to 1800).")] int? timeoutSeconds = null,
        string? resumeSession = null,
        string[]? files = null,
        [Description("Cast name to source model/tier defaults from (default: .claustrum/casts/default.json if present).")] string? cast = null,
        bool includeRaw = false,
        CancellationToken cancellationToken = default) =>
        McpExceptionBoundary.GuardAsync(async () =>
        {
            DelegateRequest request = BuildRequest(role, brief, cwd, backend, model, effort, tier, permission, deny, budgetUsd, timeoutSeconds, resumeSession, files, cast);
            RunResult result = await DelegateEngine.RunAsync(request, cancellationToken);
            RunResult output = includeRaw ? result : result with { Raw = null };

            return JsonSerializer.Serialize(output, ClaustrumJsonContext.Default.RunResult);
        });

    [McpServerTool(Name = "delegate_async")]
    [Description(
        "Like delegate, but returns immediately with a job id instead of blocking — for a task expected to run " +
        "longer than the calling host's own tool-call timeout. Only the run is deferred: config, role and model " +
        "resolution happen in this call, so an unknown role, a broken claustrum.json or a tier this role has no " +
        "model for is an error here, with no job created. Poll job_status for progress and job_result for the " +
        "final RunResult once state is 'done'.")]
    public static string DelegateStart(
        string role, string brief, string? cwd = null, string? backend = null, string? model = null, string? effort = null,
        string? tier = null, string? permission = null, string[]? deny = null, decimal? budgetUsd = null,
        [Description("Timeout in seconds (default: unset, so the config layers' defaults.timeout_seconds decides, falling back to 1800).")] int? timeoutSeconds = null,
        string? resumeSession = null, string[]? files = null, string? cast = null) =>
        McpExceptionBoundary.Guard(() =>
        {
            DelegateRequest request = BuildRequest(role, brief, cwd, backend, model, effort, tier, permission, deny, budgetUsd, timeoutSeconds, resumeSession, files, cast);
            // Deliberately CancellationToken.None: the job must outlive this tool call's own
            // request, which is what "returns immediately" means — a client cancelling *this* call
            // cannot reach back into an already-started background job.
            (string jobId, string logPath) = AppServices.JobManager.Start(request, CancellationToken.None);

            return JsonSerializer.Serialize(new DelegateAsyncResult(jobId, logPath), McpJsonContext.Default.DelegateAsyncResult);
        });

    [McpServerTool(Name = "coordinate")]
    [Description(
        "Spawn the cast's architect headlessly (docs/PLAN.md §D3) over GitHub issues or a brief: it reads the " +
        "cast, writes the briefs and delegates builders/reviewers/tester itself. Async by nature — returns " +
        "{job_id, log_path} immediately, progress via job_status (state, elapsed seconds and the architect's " +
        "last output line), the architect's RunResult via job_result once state is 'done'. The returned job_id " +
        "is also the budget tree id: every child the architect spawns is accounted against this cast's " +
        "budget_usd and inspectable with `claustrum jobs budget <job_id>`. Pass exactly one of issues or brief. " +
        "Needs `gh` on PATH for issues (see the doctor tool).")]
    public static Task<string> CoordinateAsync(
        [Description("GitHub issue numbers to import as the task, e.g. [12, 13].")] int[]? issues = null,
        [Description("Task text, instead of issues.")] string? brief = null,
        [Description("Working directory (default: the server's own cwd).")] string? cwd = null,
        [Description("Cast name (default: .claustrum/casts/default.json).")] string? cast = null,
        [Description("Architect tier: high (default), xhigh, or max.")] string? tier = null,
        [Description("Architect model override.")] string? model = null,
        [Description("Budget cap in USD for the architect's own run; the cast's budget_usd caps the whole tree.")] decimal? budgetUsd = null,
        [Description("Timeout in seconds (default: unset, so the config layers' defaults.timeout_seconds decides, falling back to 1800).")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        McpExceptionBoundary.GuardAsync(async () =>
        {
            string resolvedCwd = cwd is { Length: > 0 } ? Path.GetFullPath(cwd) : Environment.CurrentDirectory;
            CoordinateRequest request = new(
                Cwd: resolvedCwd,
                CastName: cast,
                Issues: issues ?? [],
                Brief: brief,
                TierFlag: tier,
                Overrides: new ConfigOverrides(Model: model, BudgetUsd: budgetUsd, TimeoutSeconds: timeoutSeconds),
                // Streaming with no OnStreamLine: nothing echoes it anywhere, but ProcessRunner logs
                // every line as it arrives, so job_status has a last line to relay for the whole run
                // instead of an empty stdout.log until exit (claude's Parse auto-detects the JSONL).
                Stream: true,
                DiffCapBytes: McpDiffCapBytes);

            // Planned on this call, not inside the job: a usage error, a missing cast or a failed
            // `gh` is then answered here, with its own message, instead of being buried in a
            // job_result nobody knows to ask for — and no job directory is minted for it.
            CoordinatePlan plan = await CoordinateEngine.PlanAsync(request, new GhIssueSource(AppServices.Platform), cancellationToken);

            // Config, role render and model alias too (issue #23): a broken claustrum.json or a tier
            // the architect lacks must be answered on this call, with no job minted for it either.
            PreparedDelegation prepared = plan.Prepare();

            // The job id has to exist before the run does — it is the tree the architect's children
            // join, and DelegateRequest.JobIdToken stands in for it until here — so this takes
            // JobManager's JobPaths-first overload. CancellationToken.None for the same reason
            // delegate_async does: the job outlives this tool call.
            (string jobId, string logPath) = AppServices.JobManager.Start(
                (job, token) => CoordinateEngine.RunAsync(plan, prepared, job, token), CancellationToken.None);

            return JsonSerializer.Serialize(new DelegateAsyncResult(jobId, logPath), McpJsonContext.Default.DelegateAsyncResult);
        });

    [McpServerTool(Name = "job_status")]
    [Description(
        "Check a delegate_async job's progress: state (running|done|failed), elapsed seconds, and the last " +
        "captured output line. 'failed' means the job threw after it was started instead of finishing with a " +
        "RunResult (e.g. a blind-gate rejection) — call job_result for the underlying error. State 'unknown' " +
        "means no such job id.")]
    public static string JobStatus(string jobId)
    {
        JobStatusInfo status = AppServices.JobManager.GetStatus(jobId) ?? new JobStatusInfo("unknown", 0, null);
        return JsonSerializer.Serialize(status, McpJsonContext.Default.JobStatusInfo);
    }

    [McpServerTool(Name = "job_result")]
    [Description("Get a delegate_async job's RunResult once it has finished. Throws if the job id is unknown or has not finished yet (call job_status first if unsure), or rethrows the job's own exception if it failed before producing a RunResult.")]
    public static Task<string> JobResultAsync(string jobId) =>
        McpExceptionBoundary.GuardAsync(async () =>
        {
            RunResult result = await AppServices.JobManager.GetResultAsync(jobId)
                ?? throw new McpException($"job '{jobId}' not found or not finished yet — check job_status first");

            return JsonSerializer.Serialize(result, ClaustrumJsonContext.Default.RunResult);
        });

    private static DelegateRequest BuildRequest(
        string role, string brief, string? cwd, string? backend, string? model, string? effort, string? tier,
        string? permission, string[]? deny, decimal? budgetUsd, int? timeoutSeconds, string? resumeSession, string[]? files, string? cast)
    {
        string resolvedCwd = cwd is { Length: > 0 } ? Path.GetFullPath(cwd) : Environment.CurrentDirectory;
        ConfigOverrides overrides = new(Backend: backend, Model: model, Effort: effort, Permission: permission, Deny: deny, BudgetUsd: budgetUsd, TimeoutSeconds: timeoutSeconds);
        (string resolvedTier, ConfigOverrides resolvedOverrides, CastBudget? castBudget, int? maxParallel, string? resolvedCastName) = CastApplication.Resolve(resolvedCwd, role, cast, tier, overrides);

        return new DelegateRequest(
            Role: role,
            Brief: brief,
            Cwd: resolvedCwd,
            Tier: resolvedTier,
            Overrides: resolvedOverrides,
            ResumeSession: resumeSession,
            AttachFiles: files ?? [],
            Env: [],
            Stream: false,
            DiffCapBytes: McpDiffCapBytes,
            CastBudget: castBudget,
            MaxParallel: maxParallel,
            CastName: resolvedCastName);
    }

    [McpServerTool(Name = "list_roles")]
    [Description("List every role in the embedded role library, with its description, blind flag, and supported harnesses.")]
    public static string ListRoles()
    {
        string cwd = Environment.CurrentDirectory;
        RoleSummary[] summaries = [.. AppServices.RoleLibrary.ListRoles().Select(role =>
        {
            RoleDefinition definition = AppServices.RoleLibrary.LoadRole(role, cwd).Definition;
            return new RoleSummary(definition.Name, definition.Description, definition.Blind, definition.Harnesses);
        })];

        return JsonSerializer.Serialize(summaries, McpJsonContext.Default.RoleSummaryArray);
    }

    [McpServerTool(Name = "list_backends")]
    [Description("List the names of every registered backend (only 'claude' exists until M3).")]
    public static string ListBackends()
    {
        string[] names = [.. AppServices.Backends.All.Select(backend => backend.Name)];
        return JsonSerializer.Serialize(names, McpJsonContext.Default.StringArray);
    }

    [McpServerTool(Name = "doctor")]
    [Description("Check every registered backend's availability (found on PATH, version) so the caller can choose a backend deliberately before delegating.")]
    public static Task<string> DoctorAsync(CancellationToken cancellationToken) =>
        McpExceptionBoundary.GuardAsync(async () =>
        {
            Config config = Config.Load(AppServices.Platform, Environment.CurrentDirectory);
            List<BackendDoctorEntry> entries = [];
            foreach (IBackend backend in AppServices.Backends.All)
            {
                BackendConfig? backendConfig = config.Merged.Backends?.GetValueOrDefault(backend.Name);
                Doctor doctor = await backend.DetectAsync(backendConfig, cancellationToken);
                entries.Add(new BackendDoctorEntry(backend.Name, doctor.Found, doctor.Path, doctor.Version, doctor.Problems));
            }

            return JsonSerializer.Serialize(new DoctorReport([.. entries]), McpJsonContext.Default.DoctorReport);
        });

    [McpServerTool(Name = "cast_questions")]
    [Description("Get the cast questionnaire (docs/PLAN.md §D2): one question per role in the library plus budget, with live options filtered to backends actually found on this machine.")]
    public static Task<string> CastQuestionsAsync(CancellationToken cancellationToken) =>
        McpExceptionBoundary.GuardAsync(async () =>
        {
            string cwd = Environment.CurrentDirectory;
            Config config = Config.Load(AppServices.Platform, cwd);
            CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(AppServices.RoleLibrary, AppServices.Backends, config, cwd, cancellationToken);

            return JsonSerializer.Serialize(result, CastJsonContext.Default.CastQuestionnaireResult);
        });

    [McpServerTool(Name = "cast_create")]
    [Description("Create a cast from answered cast_questions (docs/PLAN.md §D1/§D2): answers keys must match each question's key ('architect', 'builder', 'code-reviewer', ..., 'budget'); a role's value may be 'not needed', and budget may be 'no cap'.")]
    public static string CastCreate(
        [Description("Question key -> answer.")] Dictionary<string, string> answers,
        [Description("Cast name (default: 'default', which run/delegate use automatically when no --cast/cast is given).")] string name = "default") =>
        McpExceptionBoundary.Guard(() =>
        {
            Cast cast = CastBuilder.FromAnswers(name, AppServices.RoleLibrary.Version, AppServices.RoleLibrary.ListRoles(), answers);
            CastStore.Save(Environment.CurrentDirectory, cast);

            return JsonSerializer.Serialize(cast, CastJsonContext.Default.Cast);
        });

    [McpServerTool(Name = "cast_list")]
    [Description("List the names of every cast saved under .claustrum/casts/ in the current working directory.")]
    public static string CastList()
    {
        string[] names = CastStore.ListNames(Environment.CurrentDirectory);
        return JsonSerializer.Serialize(names, McpJsonContext.Default.StringArray);
    }
}
