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
        string logPath = Path.Combine(jobDir, "stdout.log");
        string resultPath = Path.Combine(jobDir, "result.json");

        if (!Directory.Exists(jobDir))
        {
            Console.Error.WriteLine($"job '{jobId}' not found under {jobsRoot}");
            return ExitCodes.Usage;
        }

        // Print header: job id, role, backend:model
        string header = SummarizeJob(jobDir, jobId);
        SafeConsole.Error(header);

        // Pick the formatter from backend.json when available.
        IStreamFormatter? formatter = rawMode ? null : ResolveFormatter(jobDir);

        // Read what already exists in the log, then poll for new content.
        long lastPosition = 0;
        string partialLine = "";

        if (File.Exists(logPath))
            (lastPosition, partialLine) = PrintTail(logPath, 0, partialLine, formatter);

        // Poll until result.json appears.
        while (!File.Exists(resultPath))
        {
            await Task.Delay(PollIntervalMs, ct);
            (lastPosition, partialLine) = PrintTail(logPath, lastPosition, partialLine, formatter);
        }

        // Read any remaining log content after the result.
        (_, _) = PrintTail(logPath, lastPosition, partialLine, formatter);

        // Print summary line.
        PrintSummary(resultPath);

        return ExitCodes.Ok;
    }

    private static async Task<int> FollowAllAsync(string jobsRoot, bool rawMode, CancellationToken ct)
    {
        // Track which job directories we already discovered.
        Dictionary<string, JobTracker> trackers = new(StringComparer.Ordinal);

        while (!ct.IsCancellationRequested)
        {
            // Poll for new job directories.
            if (Directory.Exists(jobsRoot))
            {
                foreach (string dir in Directory.EnumerateDirectories(jobsRoot))
                {
                    string id = Path.GetFileName(dir);
                    if (id.Length < 32 || trackers.ContainsKey(id))
                        continue;

                    // Only follow jobs whose stdout.log exists (they started writing).
                    string logPath = Path.Combine(dir, "stdout.log");
                    if (!File.Exists(logPath))
                        continue;

                    string shortId = id[..Math.Min(id.Length, 23)];
                    trackers[id] = new JobTracker(dir, id, shortId, rawMode ? null : ResolveFormatter(dir));
                    SafeConsole.Error($"following {shortId}  {SummarizeJob(dir, id)}");
                }
            }

            // Read new data for every tracker.
            foreach ((_, JobTracker tracker) in trackers)
            {
                if (tracker.Done)
                    continue;

                string resultPath = Path.Combine(tracker.Directory, "result.json");
                if (File.Exists(resultPath))
                {
                    tracker.Done = true;
                    // Read any remaining output.
                    (_, _) = PrintTail(
                        Path.Combine(tracker.Directory, "stdout.log"),
                        tracker.Position, tracker.Partial, tracker.Formatter,
                        tracker.ShortId);
                    PrintSummary(resultPath);
                    continue;
                }

                (long pos, string partial) = PrintTail(
                    Path.Combine(tracker.Directory, "stdout.log"),
                    tracker.Position, tracker.Partial, tracker.Formatter,
                    tracker.ShortId);
                tracker.Position = pos;
                tracker.Partial = partial;
            }

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

    private sealed class JobTracker(string directory, string id, string shortId, IStreamFormatter? formatter)
    {
        public string Directory => directory;
        public string Id => id;
        public string ShortId => shortId;
        public IStreamFormatter? Formatter => formatter;
        public long Position;
        public string Partial = "";
        public bool Done;
    }

    // Read new data from the log, format lines (optionally with a job-id prefix for the all-jobs mode),
    // return the new file position and any held-back partial line.
    private static (long Position, string Partial) PrintTail(
        string logPath, long lastPosition, string partialLine, IStreamFormatter? formatter, string? prefix = null)
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
            string clean = line.TrimEnd('\r');
            if (formatter is { } fmt)
            {
                string? formatted = fmt.FormatLine(clean);
                if (formatted is not null)
                {
                    if (prefix is not null)
                        SafeConsole.Error($"{prefix} {formatted}");
                    else
                        SafeConsole.Error(formatted);
                }
            }
            else
            {
                if (prefix is not null)
                    SafeConsole.Error($"{prefix} {clean}");
                else
                    SafeConsole.Error(clean);
            }
        }

        return (newLength, newPartial);
    }

    // Build a header line for a job.
    private static string SummarizeJob(string jobDir, string jobId)
    {
        string resultPath = Path.Combine(jobDir, "result.json");
        string requestPath = Path.Combine(jobDir, "request.json");
        string backendPath = Path.Combine(jobDir, "backend.json");

        // Try backend.json first (written at job start).
        string backend = "";
        string model = "";
        if (File.Exists(backendPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(backendPath));
                JsonElement root = doc.RootElement;
                backend = root.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "" : "";
                model = root.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? "" : "";
            }
            catch (JsonException) { }
        }

        // Fall back to request.json when the run has not yet produced backend.json.
        string role = "";
        if (backend.Length == 0 && File.Exists(requestPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(requestPath));
                JsonElement root = doc.RootElement;
                role = root.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "" : "";
            }
            catch (JsonException) { }
        }

        // Fall back to result.json as a last resort.
        if (backend.Length == 0 && File.Exists(resultPath))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultPath));
                JsonElement root = doc.RootElement;
                backend = root.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "" : "";
                model = root.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? "" : "";
                role = root.TryGetProperty("role", out JsonElement r) ? r.GetString() ?? "" : "";
            }
            catch (JsonException) { }
        }

        string line = $"job {jobId}";
        if (role.Length > 0) line += $"  role {role}";
        if (backend.Length > 0) line += $"  {backend}:{model}";

        return line;
    }

    // Print the summary line from a completed job's result.json.
    private static void PrintSummary(string resultPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement root = doc.RootElement;

            string status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "?" : "?";
            string cost = root.TryGetProperty("cost_usd", out JsonElement c) && c.TryGetDecimal(out decimal costVal)
                ? BudgetLedger.Dollars(costVal)
                : "-";
            double? duration = root.TryGetProperty("duration_seconds", out JsonElement d) && d.TryGetDouble(out double durVal)
                ? durVal
                : null;
            string backend = root.TryGetProperty("backend", out JsonElement b) ? b.GetString() ?? "" : "";
            string model = root.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? "" : "";
            int warnings = 0;
            if (root.TryGetProperty("warnings", out JsonElement w) && w.ValueKind == JsonValueKind.Array)
                warnings = w.GetArrayLength();

            string durationStr = duration is { } dur ? $"{dur:F1}s" : "?";
            string summary = $"━━━ {status}  cost {cost}  {durationStr}  {backend}:{model}";

            if (warnings > 0)
                summary += $"  {warnings} warning{(warnings == 1 ? "" : "s")}";

            SafeConsole.Error(summary);
        }
        catch (JsonException)
        {
            SafeConsole.Error($"━━━ incomplete (unparsable result.json)");
        }
    }

    // Resolve the stream formatter for a job by reading backend.json.
    private static IStreamFormatter? ResolveFormatter(string jobDir)
    {
        string backendPath = Path.Combine(jobDir, "backend.json");
        if (!File.Exists(backendPath))
            return null;

        string? backendName;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(backendPath));
            backendName = doc.RootElement.TryGetProperty("backend", out JsonElement b) ? b.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (backendName is null)
            return null;

        // Fall back to result.json to learn the cwd for path shortening.
        string? cwd = ReadCwd(jobDir);
        cwd ??= "";

        if (AppServices.Backends.TryGet(backendName, out IBackend? backend))
            return backend.CreateStreamFormatter(cwd);

        return null;
    }

    private static string? ReadCwd(string jobDir)
    {
        string requestPath = Path.Combine(jobDir, "request.json");
        if (!File.Exists(requestPath))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(requestPath));
            return doc.RootElement.TryGetProperty("cwd", out JsonElement cwd) ? cwd.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
