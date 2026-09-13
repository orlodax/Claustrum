using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Core.Jobs;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum jobs list [--last N]|show <id>|logs <id> [--stderr]`, reading
// `~/.claustrum/jobs` (`CLAUSTRUM_HOME` honoured via JobDirectory.ResolveRoot).
public static class JobsCommands
{
    private const int DefaultLast = 20;

    public static Command Build()
    {
        Option<int> last = new("--last") { Description = "How many recent jobs to show.", DefaultValueFactory = _ => DefaultLast };
        Command list = new("list", "List recent jobs.") { last };
        list.SetAction(parseResult => List(parseResult.GetValue(last)));

        Argument<string> showId = new("id") { Description = "Job id." };
        Command show = new("show", "Show one job's result (or request, if it has not finished).") { showId };
        show.SetAction(parseResult => Show(parseResult.GetRequiredValue(showId)));

        Argument<string> logsId = new("id") { Description = "Job id." };
        Option<bool> stderrOption = new("--stderr") { Description = "Show stderr.log instead of stdout.log." };
        Command logs = new("logs", "Print a job's captured output.") { logsId, stderrOption };
        logs.SetAction(parseResult => Logs(parseResult.GetRequiredValue(logsId), parseResult.GetValue(stderrOption)));

        return new Command("jobs", "Inspect past and running jobs.") { list, show, logs };
    }

    private static int List(int last)
    {
        string root = JobDirectory.ResolveRoot(CliServices.Platform);
        if (!Directory.Exists(root))
        {
            Console.WriteLine("(no jobs yet)");
            return ExitCodes.Ok;
        }

        IEnumerable<string> ids = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderDescending(StringComparer.Ordinal)
            .Take(last);

        foreach (string id in ids)
            Console.WriteLine($"{id}  {Summarize(root, id)}");

        return ExitCodes.Ok;
    }

    private static string Summarize(string root, string id)
    {
        string resultPath = Path.Combine(root, id, "result.json");
        if (!File.Exists(resultPath))
            return "pending (no result.json)";

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath));
        JsonElement rootElement = document.RootElement;
        string status = rootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "?" : "?";
        string role = rootElement.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "?" : "?";
        string backend = rootElement.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "?" : "?";
        return $"{status,-16} {role}/{backend}";
    }

    private static int Show(string id)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(CliServices.Platform), id);
        string resultPath = Path.Combine(directory, "result.json");
        string requestPath = Path.Combine(directory, "request.json");

        if (File.Exists(resultPath))
        {
            PrintIndentedJson(File.ReadAllText(resultPath));
            return ExitCodes.Ok;
        }

        if (File.Exists(requestPath))
        {
            Console.WriteLine("(no result yet)");
            PrintIndentedJson(File.ReadAllText(requestPath));
            return ExitCodes.Ok;
        }

        Console.Error.WriteLine($"job '{id}' not found under {JobDirectory.ResolveRoot(CliServices.Platform)}");
        return ExitCodes.Usage;
    }

    private static int Logs(string id, bool showStderr)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(CliServices.Platform), id);
        string logPath = Path.Combine(directory, showStderr ? "stderr.log" : "stdout.log");

        if (!File.Exists(logPath))
        {
            Console.Error.WriteLine($"no {(showStderr ? "stderr.log" : "stdout.log")} for job '{id}'");
            return ExitCodes.Usage;
        }

        Console.Write(File.ReadAllText(logPath));
        return ExitCodes.Ok;
    }

    // Plain JsonDocument.WriteTo, not JsonSerializer: this only re-formats bytes already on disk,
    // so it needs no JsonTypeInfo and stays AOT-safe without touching ClaustrumJsonContext.
    private static void PrintIndentedJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            document.RootElement.WriteTo(writer);

        Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
