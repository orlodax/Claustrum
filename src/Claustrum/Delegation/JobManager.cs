using System.Collections.Concurrent;
using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;

namespace Claustrum.Delegation;

public sealed record JobStatusInfo(string State, double ElapsedSeconds, string? LastLine);

// Backs MCP delegate_async/job_status/job_result (docs/PLAN.md §A6). In-memory only, so it covers
// jobs started via delegate_async in this MCP server's own lifetime; job_status/job_result still
// answer for any other job id by reading its result.json off disk (same as `claustrum jobs show`),
// just without a "running" state for a job this process never started itself.
public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, Entry> jobs = new();

    public (string JobId, string LogPath) Start(DelegateRequest request, CancellationToken cancellationToken)
    {
        JobPaths job = JobDirectory.Create(AppServices.Platform);
        Task<RunResult> task = DelegateEngine.RunAsync(request, job, cancellationToken);
        jobs[job.Id] = new Entry(job, task, DateTimeOffset.UtcNow);
        return (job.Id, job.StdoutLog);
    }

    public JobStatusInfo? GetStatus(string jobId)
    {
        if (jobs.TryGetValue(jobId, out Entry? entry))
        {
            string state = entry.Task.IsCompleted ? "done" : "running";
            double elapsed = (DateTimeOffset.UtcNow - entry.StartedAt).TotalSeconds;
            return new JobStatusInfo(state, elapsed, LastLine(entry.Job.StdoutLog));
        }

        string resultPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "result.json");
        return File.Exists(resultPath) ? new JobStatusInfo("done", 0, null) : null;
    }

    public async Task<RunResult?> GetResultAsync(string jobId)
    {
        if (jobs.TryGetValue(jobId, out Entry? entry))
            return await entry.Task;

        string resultPath = Path.Combine(JobDirectory.ResolveRoot(AppServices.Platform), jobId, "result.json");
        return File.Exists(resultPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(resultPath), ClaustrumJsonContext.Default.RunResult)
            : null;
    }

    // ProcessRunner opens stdout.log with FileShare.Read (StreamWriter(path, append) default), so a
    // concurrent read here while the backend is still running is safe — the line read back may be
    // mid-write, which is fine for a best-effort progress indicator.
    private static string? LastLine(string logPath) =>
        File.Exists(logPath) ? File.ReadLines(logPath).LastOrDefault() : null;

    private sealed record Entry(JobPaths Job, Task<RunResult> Task, DateTimeOffset StartedAt);
}
