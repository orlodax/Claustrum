using System.CommandLine;
using System.Text;
using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;

namespace Claustrum.Cli;

// docs/PLAN.md §A5 `claustrum jobs follow [<id>] [--raw]`: watch a job's stdout.log grow, formatted
// per its backend's stream formatter, or watch every new job that starts after the command.
public static class JobsFollowCommand
{
    private const int PollIntervalMs = 500;

    public static Command Build()
    {
        Argument<string?> id = new("id")
        {
            Description = "Job id to follow (omit to follow every new job).",
            Arity = ArgumentArity.ZeroOrOne,
        };

        Option<bool> raw = new("--raw") { Description = "Show unformatted raw stream lines." };

        Command follow = new("follow", "Follow a job's output as it runs (poll-based, ~500 ms).")
        {
            id, raw,
        };

        follow.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetValue(id),
            parseResult.GetValue(raw)));

        return follow;
    }

    private static async Task<int> ExecuteAsync(string? jobId, bool rawMode)
    {
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };
        CancellationToken ct = cts.Token;

        string jobsRoot = JobDirectory.ResolveRoot(AppServices.Platform);

        try
        {
            if (jobId is { Length: > 0 })
                return await FollowOneAsync(jobsRoot, jobId, rawMode, ct);
            else
                return await FollowAllAsync(jobsRoot, rawMode, ct);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
    }

    private static async Task<int> FollowOneAsync(string jobsRoot, string jobId, bool rawMode, CancellationToken ct)
    {
        string jobDir = Path.Combine(jobsRoot, jobId);
        if (!Directory.Exists(jobDir))
        {
            Console.Error.WriteLine($"job '{jobId}' not found under {jobsRoot}");
            return ExitCodes.Usage;
        }

        // Print header: job id, role, backend:model
        SafeConsole.Error(SummarizeJob(jobDir, jobId));

        JobTracker job = new(jobDir, rawMode);
        while (!Poll(job, prefix: null))
            await Task.Delay(PollIntervalMs, ct);

        return ExitCodes.Ok;
    }

    private static async Task<int> FollowAllAsync(string jobsRoot, bool rawMode, CancellationToken ct)
    {
        // Jobs already on disk are history, not news: only directories created after this point are followed.
        HashSet<string> existing = new(StringComparer.Ordinal);
        if (Directory.Exists(jobsRoot))
            foreach (string dir in Directory.EnumerateDirectories(jobsRoot))
                existing.Add(Path.GetFileName(dir));

        Dictionary<string, JobTracker> trackers = new(StringComparer.Ordinal);

        while (!ct.IsCancellationRequested)
        {
            if (Directory.Exists(jobsRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(jobsRoot))
                {
                    string id = Path.GetFileName(dir);
                    if (existing.Contains(id) || trackers.ContainsKey(id))
                        continue;

                    // Only follow jobs whose stdout.log exists (they started writing).
                    if (!File.Exists(Path.Combine(dir, "stdout.log")))
                        continue;

                    trackers[id] = new JobTracker(dir, rawMode);
                    SafeConsole.Error($"following {id}  {SummarizeJob(dir, id)}");
                }
            }

            foreach ((string id, JobTracker tracker) in trackers)
                if (!tracker.Done)
                    tracker.Done = Poll(tracker, prefix: id);

            try
            {
                await Task.Delay(PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return ExitCodes.Cancelled;
    }

    private sealed class JobTracker(string directory, bool rawMode)
    {
        private const int MaxSummaryAttempts = 20;

        public string Directory => directory;
        public IStreamFormatter? Formatter { get; private set; }
        public long Position;
        public string Partial = "";
        public bool Done;
        private bool formatterResolved = rawMode;
        private int summaryAttempts;

        // backend.json is written at job start but may not be there yet (or fully written) when a
        // follow starts right after `delegate_async` returned the id: ask again on every poll.
        public bool TryResolveFormatter()
        {
            if (!formatterResolved)
                (formatterResolved, Formatter) = ResolveFormatter(directory);

            return formatterResolved;
        }

        // result.json is written non-atomically, so a failed read is retried on the next poll; only a
        // file that stays unreadable for ~10 s is reported as incomplete.
        public bool SummaryGivenUp() => ++summaryAttempts >= MaxSummaryAttempts;
    }

    // One poll of one job: print what the log gained, and once result.json is there, the summary.
    // Returns true when the job is finished and reported.
    private static bool Poll(JobTracker job, string? prefix)
    {
        string resultPath = Path.Combine(job.Directory, "result.json");

        // Checked before the tail: result.json is written after the process exits, so a log read that
        // follows it sees the whole output.
        bool finished = File.Exists(resultPath);

        // Until backend.json names the formatter the log is held back; a finished job without one is raw.
        if (job.TryResolveFormatter() || finished)
            (job.Position, job.Partial) = PrintTail(Path.Combine(job.Directory, "stdout.log"), job.Position, job.Partial, job.Formatter, prefix);

        if (!finished)
            return false;

        if (TryPrintSummary(resultPath))
            return true;

        if (!job.SummaryGivenUp())
            return false;

        SafeConsole.Error("━━━ incomplete (unparsable result.json)");
        return true;
    }

    // Read new data from the log, format lines (optionally with a job-id prefix for the all-jobs mode),
    // return the new file position and any held-back partial line.
    private static (long Position, string Partial) PrintTail(
        string logPath, long lastPosition, string partialLine, IStreamFormatter? formatter, string? prefix)
    {
        if (!File.Exists(logPath))
            return (0, partialLine);

        long length;
        try
        {
            length = new FileInfo(logPath).Length;
        }
        catch (IOException)
        {
            return (lastPosition, partialLine);
        }

        if (length <= lastPosition)
            return (length, partialLine);

        long newLength = length;
        byte[] buffer = new byte[newLength - lastPosition];
        int read = 0;

        try
        {
            using FileStream fs = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(lastPosition, SeekOrigin.Begin);

            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
        }
        catch (IOException)
        {
            return (lastPosition, partialLine);
        }

        string text = Encoding.UTF8.GetString(buffer, 0, read);
        string combined = partialLine + text;

        // Find the last newline: everything after it is a partial line to hold back.
        int lastNewline = combined.LastIndexOf('\n');
        if (lastNewline < 0)
            return (newLength, combined); // No complete line yet.

        string complete = combined[..lastNewline];
        string newPartial = combined[(lastNewline + 1)..];
        string[] lines = complete.Split('\n');

        foreach (string line in lines)
        {
            string? output = FormatSafely(formatter, line.TrimEnd('\r'));
            if (output is null)
                continue;

            SafeConsole.Error(prefix is null ? output : $"{prefix} {output}");
        }

        return (newLength, newPartial);
    }

    // A formatter that chokes on a line must not take the follow down: that line goes out raw.
    private static string? FormatSafely(IStreamFormatter? formatter, string line)
    {
        if (formatter is null)
            return line;

        try
        {
            return formatter.FormatLine(line);
        }
        catch (Exception)
        {
            return line;
        }
    }

    // Build a header line for a job.
    private static string SummarizeJob(string jobDir, string jobId)
    {
        // Try backend.json first (written at job start).
        string backend = "";
        string model = "";
        using (JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "backend.json")))
        {
            backend = doc is null ? "" : GetString(doc.RootElement, "backend") ?? "";
            model = doc is null ? "" : GetString(doc.RootElement, "model") ?? "";
        }

        // Fall back to request.json when the run has not yet produced backend.json.
        string role = "";
        if (backend.Length == 0)
        {
            using JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "request.json"));
            role = doc is null ? "" : GetString(doc.RootElement, "role") ?? "";
        }

        // Fall back to result.json as a last resort.
        if (backend.Length == 0)
        {
            using JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "result.json"));
            if (doc is not null)
            {
                backend = GetString(doc.RootElement, "backend") ?? "";
                model = GetString(doc.RootElement, "model") ?? "";
                role = GetString(doc.RootElement, "role") ?? "";
            }
        }

        string line = $"job {jobId}";
        if (role.Length > 0) line += $"  role {role}";
        if (backend.Length > 0) line += $"  {backend}:{model}";

        return line;
    }

    // Print the summary line from a completed job's result.json; false when it cannot be read yet.
    private static bool TryPrintSummary(string resultPath)
    {
        using JsonDocument? doc = TryReadObject(resultPath);
        if (doc is null)
            return false;

        JsonElement root = doc.RootElement;

        string status = GetString(root, "status") ?? "?";
        string cost = root.TryGetProperty("cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out decimal costVal)
            ? BudgetLedger.Dollars(costVal)
            : "-";
        double? duration = root.TryGetProperty("duration_seconds", out JsonElement d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double durVal)
            ? durVal
            : null;
        string backend = GetString(root, "backend") ?? "";
        string model = GetString(root, "model") ?? "";
        int warnings = root.TryGetProperty("warnings", out JsonElement w) && w.ValueKind == JsonValueKind.Array
            ? w.GetArrayLength()
            : 0;

        string durationStr = duration is { } dur ? $"{dur:F1}s" : "?";
        string summary = $"━━━ {status}  cost {cost}  {durationStr}  {backend}:{model}";

        if (warnings > 0)
            summary += $"  {warnings} warning{(warnings == 1 ? "" : "s")}";

        SafeConsole.Error(summary);
        return true;
    }

    // Resolve the stream formatter for a job by reading backend.json. Resolved is false while the file
    // is missing or not yet readable; a backend with no formatter is resolved, to null.
    private static (bool Resolved, IStreamFormatter? Formatter) ResolveFormatter(string jobDir)
    {
        using JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "backend.json"));
        if (doc is null || GetString(doc.RootElement, "backend") is not { } backendName)
            return (false, null);

        // The cwd shortens paths in the formatted lines; request.json is written before backend.json.
        string cwd = ReadCwd(jobDir) ?? "";

        return AppServices.Backends.TryGet(backendName, out IBackend? backend)
            ? (true, backend.CreateStreamFormatter(cwd))
            : (true, null);
    }

    private static string? ReadCwd(string jobDir)
    {
        using JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "request.json"));
        return doc is null ? null : GetString(doc.RootElement, "cwd");
    }

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    // Null when the file is missing, mid-write (a partial document), locked, or not a JSON object.
    private static JsonDocument? TryReadObject(string path)
    {
        try
        {
            JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc;

            doc.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
