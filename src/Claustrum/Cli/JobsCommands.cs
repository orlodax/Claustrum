using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum jobs list [--last N]|show <id>|logs <id> [--stderr]|clean`, reading
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

        Option<string?> cleanCwd = new("--cwd") { Description = "Working directory (default: current directory)." };
        Command clean = new("clean", "Remove finished max_parallel jobs' worktrees (docs/PLAN.md §D4); their branches are kept.") { cleanCwd };
        clean.SetAction(async parseResult => await CleanAsync(parseResult.GetValue(cleanCwd)));

        return new Command("jobs", "Inspect past and running jobs.") { list, show, logs, clean };
    }

    private static int List(int last)
    {
        string root = JobDirectory.ResolveRoot(AppServices.Platform);
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

        // A job killed mid-write (crash, `kill -9`, power loss) leaves a truncated/partial
        // result.json; one bad job must not take the whole `jobs list` down (review finding #5).
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement rootElement = document.RootElement;
            string status = rootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "?" : "?";
            string role = rootElement.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "?" : "?";
            string backend = rootElement.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "?" : "?";
            return $"{status,-16} {role}/{backend}";
        }
        catch (JsonException)
        {
            return "incomplete (unparsable result.json)";
        }
    }

    private static int Show(string id)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), id);
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

        Console.Error.WriteLine($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}");
        return ExitCodes.Usage;
    }

    private static int Logs(string id, bool showStderr)
    {
        string directory = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), id);
        string logPath = Path.Combine(directory, showStderr ? "stderr.log" : "stdout.log");

        if (!File.Exists(logPath))
        {
            Console.Error.WriteLine($"no {(showStderr ? "stderr.log" : "stdout.log")} for job '{id}'");
            return ExitCodes.Usage;
        }

        Console.Write(File.ReadAllText(logPath));
        return ExitCodes.Ok;
    }

    // A worktree's directory name IS the job id that created it (JobWorktree.PathFor); IsCleanable
    // below decides which ones are done with. The branch is never touched here (JobWorktree.RemoveAsync
    // only removes the working directory), so the architect can still rebase from it afterwards.
    private static async Task<int> CleanAsync(string? cwdOption)
    {
        string cwd = Path.GetFullPath(cwdOption ?? Environment.CurrentDirectory);
        string worktreesRoot = Path.Combine(cwd, ".claustrum", "worktrees");
        if (!Directory.Exists(worktreesRoot))
        {
            Console.WriteLine("(no worktrees)");
            return ExitCodes.Ok;
        }

        string jobsRoot = JobDirectory.ResolveRoot(AppServices.Platform);
        int removed = 0;
        List<string> failures = [];
        foreach (string worktreeDirectory in Directory.EnumerateDirectories(worktreesRoot))
        {
            string jobId = Path.GetFileName(worktreeDirectory);
            if (!IsCleanable(jobsRoot, jobId))
                continue;

            // Per entry, not per sweep: one directory git no longer recognises as a worktree (removed
            // by hand, or left by a hard kill) used to abort the whole command, so every stale
            // worktree after it stayed forever — and every rerun stopped at the same one (review
            // finding). Report it and keep going.
            try
            {
                await JobWorktree.RemoveAsync(cwd, jobId, CancellationToken.None);
                Console.WriteLine($"removed {jobId} (branch {JobWorktree.BranchFor(jobId)} kept)");
                removed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                failures.Add($"{jobId}: {ex.Message}");
            }
        }

        if (removed == 0 && failures.Count == 0)
            Console.WriteLine("(nothing to clean)");

        foreach (string failure in failures)
            Console.Error.WriteLine($"could not remove {failure}");

        // Exit 1 when anything was left behind so a script does not read a partial sweep as a full
        // one; the worktrees that did come off are still gone.
        return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.BackendFailure;
    }

    // "Finished" is result.json existing, the same signal Summarize/Show already trust — a job still
    // mid-run has none yet and its worktree is in use. A job directory that is gone entirely is the
    // other cleanable case: the run was hard-killed before writing one, or jobs.keep_last pruned the
    // directory out from under a worktree nobody ever cleaned. Without it such a worktree is
    // unreachable forever, since the signal it is waiting for can never appear.
    private static bool IsCleanable(string jobsRoot, string jobId) =>
        !Directory.Exists(Path.Combine(jobsRoot, jobId))
        || File.Exists(Path.Combine(jobsRoot, jobId, "result.json"));

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
