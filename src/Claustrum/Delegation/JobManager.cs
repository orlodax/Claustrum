using System.Collections.Concurrent;
using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Live;

namespace Claustrum.Delegation;

// State: running | waiting_for_input (a live job's question is open, and Question carries it; #124) | abandoned (a live job
// whose claustrum process is gone, no result.json) | done | failed | unknown. A done job also says how it ended (Status, the RunResult's
// own word: "needs_input" among them) and, when it stopped to ask, what (Questions; stage 3b).
public sealed record JobStatusInfo(string State, double ElapsedSeconds, string? LastLine, QuestionInfo? Question = null, string? Status = null, ReportQuestion[]? Questions = null);

// Backs MCP delegate_async/job_status/job_result (docs/PLAN.md §A6). In-memory only, so it covers
// jobs started via delegate_async in this MCP server's own lifetime; job_status/job_result still
// answer for any other job id by reading its result.json off disk (same as `claustrum jobs show`),
// just without a "running" state for a job this process never started itself.
public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, Entry> jobs = new();

    public (string JobId, string LogPath) Start(DelegateRequest request, CancellationToken cancellationToken)
    {
        // Prepared here, before the JobPaths below exists (issue #23): a malformed claustrum.json, a
        // tier this role has no model class for or an unresolvable alias is then the delegate_async
        // tool call's own error, not a `pending (no result.json)` directory nobody will ever close.
        PreparedDelegation prepared = DelegateEngine.Prepare(request);

        return Start((job, token) => MachineryRefusedException.AsReceiptAsync(DelegateEngine.RunAsync(prepared, job, token)), cancellationToken);
    }

    /// <summary>
    /// The same background job for a caller whose run needs the job id *before* it starts:
    /// `coordinate`'s tree id is the job id it will run under (docs/PLAN.md §D3), so it reads
    /// <see cref="JobPaths.Id"/> out of the directory this creates and binds its already-prepared
    /// delegation to it (<see cref="PreparedDelegation.ForJob"/>).
    /// </summary>
    public (string JobId, string LogPath) Start(Func<JobPaths, CancellationToken, Task<RunResult>> run, CancellationToken cancellationToken)
    {
        JobPaths job = JobDirectory.Create(AppServices.Platform);
        Task<RunResult> task = run(job, cancellationToken);
        jobs[job.Id] = new Entry(job, task, DateTimeOffset.UtcNow);
        return (job.Id, job.StdoutLog);
    }

    public JobStatusInfo? GetStatus(string jobId)
    {
        if (jobs.TryGetValue(jobId, out Entry? entry))
        {
            string state = entry.Task.Status switch
            {
                TaskStatus.RanToCompletion => "done",
                TaskStatus.Faulted or TaskStatus.Canceled => "failed",
                _ => "running",
            };

            // The task decides when a job is done; live.json only adds that a running one is waiting for its human.
            // #126: a job retried on its cast's fallback is followed to the retry, whose question and last line are the ones that matter.
            LiveJob watched = LiveJob.At(entry.Job);
            if (watched.ReadRetry() is { } retry)
                watched = retry;

            QuestionInfo? question = null;
            if (state == "running" && watched.Observe() is { State: "waiting_for_input" } waiting)
                (state, question) = (waiting.State, waiting.Question);

            double elapsed = (DateTimeOffset.UtcNow - entry.StartedAt).TotalSeconds;
            RunResult? finished = entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;
            return new JobStatusInfo(state, elapsed, JobLog.LastLine(watched.LogPath), question, StatusName(finished), finished?.Questions);
        }

        // A job this process never started: its result.json says done; a live one (live.json) says what its session is doing (#124).
        if (!NamesAStoreEntry(jobId))
            return null;

        string resultPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "result.json");
        if (File.Exists(resultPath))
        {
            RunResult? receipt = LiveJob.Find(jobId)?.ReadResult();
            return new JobStatusInfo("done", 0, null, Status: StatusName(receipt), Questions: receipt?.Questions);
        }

        if (LiveJob.Find(jobId) is not { } live || live.ReadState() is null)
            return null;

        JobWaitResult observed = live.Observe();
        return new JobStatusInfo(observed.State is "waiting_for_input" or "abandoned" ? observed.State : "running", 0, null, observed.Question);
    }

    public async Task<RunResult?> GetResultAsync(string jobId)
    {
        if (jobs.TryGetValue(jobId, out Entry? entry))
            return await ResolveTaskAsync(entry.Task);

        if (!NamesAStoreEntry(jobId))
            return null;

        string resultPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "result.json");
        return File.Exists(resultPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(resultPath), ClaustrumJsonContext.Default.RunResult)
            : null;
    }

    // 2026-09-14 review finding #6: an unconditional `await entry.Task` here blocked job_result for
    // up to the run's own timeout on a job that was merely still running, contradicting its own
    // "throws if not finished yet" description. `task.IsCompleted` decides without ever awaiting a
    // pending task; a faulted task still gets `await`ed so its exception surfaces to the caller
    // instead of the misleading "not found or not finished yet" a plain null would produce. Public
    // (not a private helper) so it is unit-testable against a hand-built TaskCompletionSource — the
    // real Start()/AppServices path has no seam to force a "still running" or "faulted" task
    // deterministically without either flaky timing or actually spawning a backend process.
    public static async Task<RunResult?> ResolveTaskAsync(Task<RunResult> task) =>
        task.IsCompleted ? await task : null;

    private static string? StatusName(RunResult? result) =>
        result is null ? null : JsonSerializer.Serialize(result.Status, ClaustrumJsonContext.Default.RunStatus).Trim('"');

    // The disk fallbacks name a directory under the jobs store, so the id must be one plain name: a separator, or on Windows a drive
    // (`C:x`), would leave the store (review of #124 stage 2, F6). The strict yyyyMMdd-HHmmss-xxxxxxxx shape is the `jobs` verbs' rule.
    private static bool NamesAStoreEntry(string jobId) => jobId.Length > 0 && jobId is not ("." or "..") && jobId.AsSpan().IndexOfAny('/', '\\', ':') < 0;

    private sealed record Entry(JobPaths Job, Task<RunResult> Task, DateTimeOffset StartedAt);
}
