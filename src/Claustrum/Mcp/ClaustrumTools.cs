using System.ComponentModel;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Mcp.Json;
using Claustrum.Roles.Model;
using ModelContextProtocol.Server;

namespace Claustrum.Mcp;

// docs/PLAN.md §A6: the 10 MCP tools, thin adapters over the same DelegateEngine/JobManager/
// RoleLibrary/BackendRegistry/CastStore the CLI uses — no logic lives here that the CLI does not
// already exercise. Every tool returns a pre-serialized JSON string.
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
    public static async Task<string> DelegateAsync(
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
        int timeoutSeconds = DelegateEngine.DefaultTimeoutSeconds,
        string? resumeSession = null,
        string[]? files = null,
        [Description("Cast name to source model/tier defaults from (default: .claustrum/casts/default.json if present).")] string? cast = null,
        bool includeRaw = false,
        CancellationToken cancellationToken = default)
    {
        string resolvedCwd = cwd is { Length: > 0 } ? Path.GetFullPath(cwd) : Environment.CurrentDirectory;
        ConfigOverrides overrides = new(Backend: backend, Model: model, Effort: effort, Permission: permission, Deny: deny, BudgetUsd: budgetUsd, TimeoutSeconds: timeoutSeconds);
        (string resolvedTier, ConfigOverrides resolvedOverrides, CastBudget? castBudget) = CastApplication.Resolve(resolvedCwd, role, cast, tier, overrides);

        DelegateRequest request = new(
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
            CastBudget: castBudget);

        RunResult result = await DelegateEngine.RunAsync(request, cancellationToken);
        RunResult output = includeRaw ? result : result with { Raw = null };

        return JsonSerializer.Serialize(output, ClaustrumJsonContext.Default.RunResult);
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
    public static async Task<string> DoctorAsync(CancellationToken cancellationToken)
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
    }

    [McpServerTool(Name = "cast_questions")]
    [Description("Get the cast questionnaire (docs/PLAN.md §D2): one question per role in the library plus budget, with live options filtered to backends actually found on this machine.")]
    public static async Task<string> CastQuestionsAsync(CancellationToken cancellationToken)
    {
        string cwd = Environment.CurrentDirectory;
        Config config = Config.Load(AppServices.Platform, cwd);
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(AppServices.RoleLibrary, AppServices.Backends, config, cwd, cancellationToken);

        return JsonSerializer.Serialize(result, CastJsonContext.Default.CastQuestionnaireResult);
    }

    [McpServerTool(Name = "cast_create")]
    [Description("Create a cast from answered cast_questions (docs/PLAN.md §D1/§D2): answers keys must match each question's key ('architect', 'builder', 'code-reviewer', ..., 'budget'); a role's value may be 'not needed', and budget may be 'no cap'.")]
    public static string CastCreate(
        [Description("Question key -> answer.")] Dictionary<string, string> answers,
        [Description("Cast name (default: 'default', which run/delegate use automatically when no --cast/cast is given).")] string name = "default")
    {
        Cast cast = CastBuilder.FromAnswers(name, AppServices.RoleLibrary.Version, answers);
        CastStore.Save(Environment.CurrentDirectory, cast);

        return JsonSerializer.Serialize(cast, CastJsonContext.Default.Cast);
    }

    [McpServerTool(Name = "cast_list")]
    [Description("List the names of every cast saved under .claustrum/casts/ in the current working directory.")]
    public static string CastList()
    {
        string[] names = CastStore.ListNames(Environment.CurrentDirectory);
        return JsonSerializer.Serialize(names, McpJsonContext.Default.StringArray);
    }
}
