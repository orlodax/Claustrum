using System.ComponentModel;
using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
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
