namespace Claustrum.Mcp.Json;

// `job_answer`'s answer (#124 stage 3b): the new job that continues the asking one, started and running; poll it as any delegate_async job.
// Warnings are what a coordinate plan said before its run (CoordinateStartResult's), empty for any other job.
public sealed record JobAnswerResult(string JobId, string ParentJobId, string LogPath, string[] Warnings);
