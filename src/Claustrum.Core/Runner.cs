using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Git;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;
using Claustrum.Core.Report;

namespace Claustrum.Core;

// docs/PLAN.md A3: blind gate -> resolve backend -> Build -> snapshot -> ProcessRunner -> snapshot
// -> Parse -> ReportExtractor.Extract -> RunResult -> result.json -> delete temp files. "resolve"
// here is the backend-name lookup in BackendRegistry, not Config.Resolve — the caller already
// produced ResolvedRole before calling RunAsync.
public sealed class Runner(IPlatform platform, BackendRegistry backends, ProcessRunner processRunner)
{
    public async Task<RunResult> RunAsync(RunRequest request, ResolvedRole role, RunOptions options, CancellationToken cancellationToken)
    {
        string brief = ResolveBrief(request, platform);
        EnsureBlindGate(role, brief);

        JobPaths job = JobDirectory.Create(platform);
        File.WriteAllText(job.SystemMd, role.SystemPrompt);
        File.WriteAllText(job.RequestJson, JsonSerializer.Serialize(request, ClaustrumJsonContext.Default.RunRequest));

        if (!backends.TryGet(role.Backend, out IBackend? backend))
            return WriteResult(job, MissingBackendResult(job, role));

        WorktreeState before = await WorktreeSnapshot.CaptureAsync(request.Cwd, cancellationToken);

        ResolvedRun run = new(role, brief, request.Cwd, request.BudgetUsd, request.ResumeSession, request.AttachFiles, request.Stream, job.SystemMd, job.Directory);
        ProcessSpec spec = backend.Build(run);

        ProcessOutcome outcome = await processRunner.RunAsync(spec, backendConfig: null, job, options.OnStreamLine, request.Timeout, cancellationToken);

        WorktreeState after = await WorktreeSnapshot.CaptureAsync(request.Cwd, cancellationToken);
        SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(request.Cwd, before, after, options.DiffByteCapBytes, cancellationToken);

        ParsedOutput parsed = backend.Parse(outcome.Stdout, outcome.Stderr, outcome.ExitCode);
        ExtractedReport extracted = ReportExtractor.Extract(parsed.FinalMessage);
        RunStatus status = DetermineStatus(outcome, parsed);

        RunResult result = new(
            SchemaVersion: "1",
            JobId: job.Id,
            Status: status,
            Backend: role.Backend,
            Model: role.Model,
            Role: role.Name,
            FinalMessage: parsed.FinalMessage,
            ChangedFiles: diff.ChangedFiles,
            Diff: diff.Diff,
            DiffTruncated: diff.Truncated,
            SessionId: parsed.SessionId,
            CostUsd: parsed.CostUsd,
            Usage: parsed.Usage,
            ExitCode: outcome.ExitCode,
            LogPath: job.StdoutLog,
            DurationSeconds: outcome.Duration.TotalSeconds,
            Error: status == RunStatus.Success ? null : parsed.FinalMessage,
            Raw: parsed.Raw,
            Report: extracted.Report,
            ReportStatus: extracted.Status,
            Warnings: extracted.Warnings);

        DeleteTempFiles(spec.TempFiles);
        return WriteResult(job, result);
    }

    private static string ResolveBrief(RunRequest request, IPlatform platform)
    {
        if (request.Brief is { Length: > 0 })
            return request.Brief;
        if (request.BriefFile is { Length: > 0 })
            return platform.ReadAllText(request.BriefFile);

        throw new ArgumentException("RunRequest must set either Brief or BriefFile", nameof(request));
    }

    // NOTES.md "Blind review is enforced, not requested".
    private static void EnsureBlindGate(ResolvedRole role, string brief)
    {
        if (!role.Blind)
            return;

        if (brief.Contains("## Context", StringComparison.Ordinal)
            || brief.Contains("## Plan", StringComparison.Ordinal)
            || brief.Contains("## Rationale", StringComparison.Ordinal)
            || brief.Contains("claustrum-report", StringComparison.Ordinal))
            throw new BlindGateException("blind role: brief carries rationale");
    }

    private static RunResult MissingBackendResult(JobPaths job, ResolvedRole role) => new(
        SchemaVersion: "1",
        JobId: job.Id,
        Status: RunStatus.BackendMissing,
        Backend: role.Backend,
        Model: role.Model,
        Role: role.Name,
        FinalMessage: "",
        ChangedFiles: [],
        Diff: null,
        DiffTruncated: false,
        SessionId: null,
        CostUsd: null,
        Usage: null,
        ExitCode: -1,
        LogPath: job.StdoutLog,
        DurationSeconds: 0,
        Error: $"backend '{role.Backend}' is not registered",
        Raw: null,
        Report: null,
        ReportStatus: ReportStatus.Missing,
        Warnings: []);

    private static RunStatus DetermineStatus(ProcessOutcome outcome, ParsedOutput parsed) => outcome.Termination switch
    {
        ProcessTermination.TimedOut => RunStatus.Timeout,
        ProcessTermination.Cancelled => RunStatus.Cancelled,
        _ when parsed.IsError || outcome.ExitCode != 0 => RunStatus.Failed,
        _ => RunStatus.Success,
    };

    private static RunResult WriteResult(JobPaths job, RunResult result)
    {
        File.WriteAllText(job.ResultJson, JsonSerializer.Serialize(result, ClaustrumJsonContext.Default.RunResult));
        return result;
    }

    private static void DeleteTempFiles(string[] tempFiles)
    {
        foreach (string tempFile in tempFiles)
            if (File.Exists(tempFile))
                File.Delete(tempFile);
    }
}
