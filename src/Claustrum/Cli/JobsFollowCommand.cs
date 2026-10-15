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
    private const int ReadChunkBytes = 64 * 1024;

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
                    SafeConsole.Error($"following {StreamText.Sanitize(id)}  {SummarizeJob(dir, id)}");
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

        // One decoder per followed file: a multibyte character split across two polls is completed
        // by the next read instead of becoming two U+FFFD.
        public Decoder Decoder { get; } = Encoding.UTF8.GetDecoder();

        // Bytes already in the log when the follow began are a replay: a line with no time of its own
        // prints `--:--:--` instead of a made-up one. Lines past this offset are live.
        public long ReplayEnd { get; } = LogLength(Path.Combine(directory, "stdout.log"));
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
            PrintTail(job, prefix);

        if (!finished)
            return false;

        if (TryPrintSummary(resultPath))
            return true;

        if (!job.SummaryGivenUp())
            return false;

        SafeConsole.Error("━━━ incomplete (unparsable result.json)");
        return true;
    }

    private static long LogLength(string logPath)
    {
        try
        {
            return new FileInfo(logPath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Read what the log gained in bounded chunks, format its complete lines (optionally with a job-id
    // prefix for the all-jobs mode) and hold the trailing partial line back for the next poll.
    private static void PrintTail(JobTracker job, string? prefix)
    {
        string logPath = Path.Combine(job.Directory, "stdout.log");
        if (!File.Exists(logPath))
        {
            job.Position = 0;
            return;
        }

        long length = LogLength(logPath);
        if (length <= job.Position)
        {
            // A shrunk log restarts the stream, and the decoder's half-character with it.
            if (length < job.Position)
                job.Decoder.Reset();

            job.Position = length;
            return;
        }

        try
        {
            using FileStream fs = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(job.Position, SeekOrigin.Begin);

            byte[] buffer = new byte[ReadChunkBytes];
            char[] chars = new char[Encoding.UTF8.GetMaxCharCount(ReadChunkBytes)];

            while (job.Position < length)
            {
                // A chunk never straddles the replay boundary, so each one is wholly replay or wholly live.
                long limit = job.Position < job.ReplayEnd ? Math.Min(length, job.ReplayEnd) : length;
                int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - job.Position));
                if (read == 0)
                    break;

                job.Position += read;
                int count = job.Decoder.GetChars(buffer, 0, read, chars, 0);
                PrintLines(job, new string(chars, 0, count), replayed: job.Position <= job.ReplayEnd, prefix);
            }
        }
        catch (IOException)
        {
            // Position only moves with the bytes consumed, so the next poll resumes where this stopped.
        }
    }

    private static void PrintLines(JobTracker job, string text, bool replayed, string? prefix)
    {
        string combined = job.Partial + text;

        // Everything after the last newline is a partial line to hold back.
        int lastNewline = combined.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            job.Partial = combined;
            return;
        }

        job.Partial = combined[(lastNewline + 1)..];

        foreach (string line in combined[..lastNewline].Split('\n'))
        {
            string? output = FormatSafely(job.Formatter, line.TrimEnd('\r'), replayed);
            if (output is null)
                continue;

            SafeConsole.Error(prefix is null ? output : $"{StreamText.Sanitize(prefix)} {output}");
        }
    }

    // A formatter that chokes on a line must not take the follow down: that line goes out sanitized.
    private static string? FormatSafely(IStreamFormatter? formatter, string line, bool replayed)
    {
        if (formatter is null)
            return line;

        try
        {
            return formatter.FormatLine(line, replayed);
        }
        catch (Exception)
        {
            return StreamText.Sanitize(line);
        }
    }

    // Build a header line for a job: id, role, backend:model. Every part is read from job files, so each
    // goes through the terminal sanitizer.
    private static string SummarizeJob(string jobDir, string jobId)
    {
        string backend = "";
        string model = "";
        string role = "";

        // backend.json is written at job start, request.json before it; result.json fills whatever is still missing.
        using (JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "backend.json")))
        {
            backend = doc is null ? "" : GetString(doc.RootElement, "backend") ?? "";
            model = doc is null ? "" : GetString(doc.RootElement, "model") ?? "";
        }

        using (JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "request.json")))
            role = doc is null ? "" : GetString(doc.RootElement, "role") ?? "";

        if (backend.Length == 0 || role.Length == 0)
        {
            using JsonDocument? doc = TryReadObject(Path.Combine(jobDir, "result.json"));
            if (doc is not null)
            {
                if (backend.Length == 0)
                {
                    backend = GetString(doc.RootElement, "backend") ?? "";
                    model = GetString(doc.RootElement, "model") ?? "";
                }

                if (role.Length == 0)
                    role = GetString(doc.RootElement, "role") ?? "";
            }
        }

        string line = $"job {StreamText.Sanitize(jobId)}";
        if (role.Length > 0) line += $"  {StreamText.Sanitize(role)}";
        if (backend.Length > 0) line += $"  {StreamText.Sanitize(backend)}:{StreamText.Sanitize(model)}";

        return line;
    }

    // Print the summary line from a completed job's result.json; false when it cannot be read yet.
    private static bool TryPrintSummary(string resultPath)
    {
        using JsonDocument? doc = TryReadObject(resultPath);
        if (doc is null)
            return false;

        JsonElement root = doc.RootElement;

        string status = StreamText.Sanitize(GetString(root, "status") ?? "?");
        string cost = root.TryGetProperty("cost_usd", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out decimal costVal)
            ? BudgetLedger.Dollars(costVal)
            : "-";
        double? duration = root.TryGetProperty("duration_seconds", out JsonElement d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double durVal)
            ? durVal
            : null;
        string backend = StreamText.Sanitize(GetString(root, "backend"));
        string model = StreamText.Sanitize(GetString(root, "model"));
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
