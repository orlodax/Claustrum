using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
// produced ResolvedRole before calling RunAsync. Once ProcessRunner returns an outcome the backend
// process has definitely run, so everything from there on is wrapped: a throw in that section still
// yields a Failed RunResult and a written result.json instead of an unobserved crash or, on
// cancel/timeout, no result at all (NOTES.md "Runner always yields a result after the process ran").
public sealed partial class Runner(IPlatform platform, BackendRegistry backends, ProcessRunner processRunner)
{
    public async Task<RunResult> RunAsync(RunRequest request, ResolvedRole role, RunOptions options, CancellationToken cancellationToken) =>
        await RunCoreAsync(request, role, options, jobOverride: null, cancellationToken);

    // MCP delegate_async (docs/PLAN.md §A6) needs the job id *before* the run finishes, so it must
    // create the JobPaths itself and hand it in here rather than letting RunCoreAsync create one —
    // this overload is that seam; the CLI's synchronous `run` never needs it (the 4-arg overload
    // above).
    public async Task<RunResult> RunAsync(RunRequest request, ResolvedRole role, RunOptions options, JobPaths job, CancellationToken cancellationToken) =>
        await RunCoreAsync(request, role, options, jobOverride: job, cancellationToken);

    // 2026-09-14 review finding #7: the old single-expression overload evaluated
    // `JobDirectory.Create(platform)` as an *argument*, before the callee body ran — so
    // JobDirectory.Prune's deletion of old job dirs, and the job dir itself, existed before
    // ValidateTimeout/EnsureBlindGate ever ran. A routine blind-gate rejection left an empty
    // `~/.claustrum/jobs/<id>/` behind and had already pruned history. Creating `job` here, after
    // both checks, restores `main`'s original ordering while keeping delegate_async's pre-created
    // job id intact via `jobOverride`.
    private async Task<RunResult> RunCoreAsync(RunRequest request, ResolvedRole role, RunOptions options, JobPaths? jobOverride, CancellationToken cancellationToken)
    {
        ValidateTimeout(request);

        string brief = ResolveBrief(request, platform);
        brief = AppendAttachments(brief, request.AttachFiles, platform);
        EnsureBlindGate(role, brief);
        brief = AppendReportTrailer(brief, role);

        JobPaths job = jobOverride ?? JobDirectory.Create(platform);

        // docs/PLAN.md §D4: in a job tree the budget belongs to the tree, not to this run, so the
        // ledger decides before anything is written or spawned — a refusal must cost nothing. A ledger
        // that cannot be reached at all is the symmetric case to ChargeAsync's below: it leaves through
        // the same funnel as a Failed result rather than escaping RunCoreAsync as a throw.
        BudgetAdmission? admission = null;
        if (options.Tree is { } tree)
        {
            try
            {
                admission = await BudgetLedger.AdmitAsync(platform, tree, job.Id, role.Name, request.BudgetUsd);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                return await FinishAsync(job, reservation: null, FailureResult(job, role, outcome: null, ex), ran: false);
            }
        }

        // Held for the rest of this method: while this handle is open a sibling's admission reserves
        // this job's cap instead of handing the same dollars out twice (review finding F2).
        await using BudgetReservation? reservation = admission?.Reservation;

        if (admission is { Admitted: false })
            return await FinishAsync(job, reservation: null, NoProcessResult(job, role, RunStatus.BudgetExceeded, admission.Reason), ran: false);

        // Clamping here is what makes the tree cap hard per child: request.json below and the backend's
        // own --max-budget-usd carry the slice the ledger granted, not what was asked.
        if (admission is { EffectiveCap: { } granted })
            request = request with { BudgetUsd = granted };

        File.WriteAllText(job.SystemMd, role.SystemPrompt);
        File.WriteAllText(job.RequestJson, JsonSerializer.Serialize(request, ClaustrumJsonContext.Default.RunRequest));

        if (!backends.TryGet(role.Backend, out IBackend? backend))
            return await FinishAsync(job, reservation, MissingBackendResult(job, role, $"backend '{role.Backend}' is not registered"), ran: false);

        // NOTES.md "Runner's before-snapshot is now inside a guarded section too": a bad `cwd` or any
        // other pre-spawn failure here used to escape RunCoreAsync entirely. `spec` does not exist
        // yet if this fails, so there is nothing for a `finally` to clean up.
        WorktreeState before;
        ProcessSpec spec;
        try
        {
            before = await WorktreeSnapshot.CaptureAsync(request.Cwd, cancellationToken);
            ResolvedRun run = new(role, brief, request.Cwd, request.BudgetUsd, request.ResumeSession, request.AttachFiles, request.Stream, job.SystemMd, job.Directory, request.Env);
            spec = backend.Build(run);
        }
        catch (Exception ex)
        {
            return await FinishAsync(job, reservation, FailureResult(job, role, outcome: null, ex), ran: false);
        }

        ProcessOutcome outcome;
        try
        {
            outcome = await processRunner.RunAsync(spec, options.BackendConfig, job, options.EnvPassthroughAll, options.OnStreamLine, request.Timeout, cancellationToken);
        }
        catch (BackendNotFoundException ex)
        {
            // Registered but not resolvable to a binary (docs/PLAN.md §A5 exit 3), as opposed to
            // the unregistered-name branch above — same BackendMissing status either way, so a
            // caller need not distinguish "no such backend" from "backend not on PATH".
            DeleteTempFiles(spec.TempFiles);
            return await FinishAsync(job, reservation, MissingBackendResult(job, role, ex.Message), ran: false);
        }

        try
        {
            // CancellationToken.None from here on: request.Timeout/cancellationToken already fired
            // to produce this outcome, and a cancelled after-snapshot would throw before any result
            // is ever written — the "Ctrl+C produces no result" bug this section exists to close.
            WorktreeState after = await WorktreeSnapshot.CaptureAsync(request.Cwd, CancellationToken.None);
            SnapshotDiff diff = await WorktreeSnapshot.DiffAsync(request.Cwd, before, after, options.DiffByteCapBytes, CancellationToken.None);

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

            return await FinishAsync(job, reservation, result, ran: true);
        }
        catch (Exception ex)
        {
            // `ran: true` — this catch only fires after ProcessOutcome came back, so the backend did
            // burn whatever it burned even though nothing downstream could read a cost out of it.
            return await FinishAsync(job, reservation, FailureResult(job, role, outcome, ex), ran: true);
        }
        finally
        {
            DeleteTempFiles(spec.TempFiles);
        }
    }

    private static void ValidateTimeout(RunRequest request)
    {
        if (request.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new RunRequestException("--timeout must be greater than zero");
    }

    private static string ResolveBrief(RunRequest request, IPlatform platform)
    {
        if (request.Brief is { Length: > 0 })
            return request.Brief;
        if (request.BriefFile is { Length: > 0 })
            return platform.ReadAllText(request.BriefFile);

        throw new ArgumentException("RunRequest must set either Brief or BriefFile", nameof(request));
    }

    // Harness-neutral (docs/PLAN.md A2): every backend gets attachments the same way, appended to
    // the prompt text rather than passed as a native "file" concept most backends don't have.
    private static string AppendAttachments(string brief, string[] attachFiles, IPlatform platform)
    {
        if (attachFiles.Length == 0)
            return brief;

        string[] missing = [.. attachFiles.Where(path => !platform.FileExists(path))];
        if (missing.Length > 0)
            throw new RunRequestException($"--file not found: {string.Join(", ", missing)}");

        StringBuilder builder = new(brief);
        foreach (string path in attachFiles)
            builder.Append("\n\n## Attached: ").Append(path).Append('\n').Append(platform.ReadAllText(path));

        return builder.ToString();
    }

    // Tester report: `## Report format` sitting last in a long system prompt got skipped on ~half
    // of trivial one-line tasks. This trailer restates the requirement at the position models honour
    // most — the end of the user prompt — on top of (not instead of) the system-prompt section.
    private static string AppendReportTrailer(string brief, ResolvedRole role) =>
        role.HasReport
            ? $"{brief}\n\n---\nFinish your reply with the mandatory ```claustrum-report fenced JSON block from your system prompt; a reply without it is rejected."
            : brief;

    // NOTES.md "Blind review is enforced, not requested".
    private static void EnsureBlindGate(ResolvedRole role, string brief)
    {
        if (!role.Blind)
            return;

        if (brief.Contains("## Context", StringComparison.Ordinal)
            || brief.Contains("## Plan", StringComparison.Ordinal)
            || brief.Contains("## Rationale", StringComparison.Ordinal)
            || ReportFencePattern().IsMatch(brief))
            throw new BlindGateException("blind role: brief carries rationale");
    }

    // Line-start (optional indent) + 3-or-more backticks + "claustrum-report" — a bare-word
    // substring match used to trip on any brief that merely mentioned the report format by name,
    // not just one that actually pasted a fence (docs/PLAN.md B3; ReportExtractor.FencePattern is
    // the deliberately-lenient sibling that finds this same fence in the backend's own output).
    [GeneratedRegex(@"^[ \t]*`{3,}claustrum-report\b", RegexOptions.Multiline)]
    private static partial Regex ReportFencePattern();

    private static RunResult MissingBackendResult(JobPaths job, ResolvedRole role, string errorMessage) =>
        NoProcessResult(job, role, RunStatus.BackendMissing, errorMessage);

    // "Nothing ran": no diff, no cost, no report, and exit -1 standing in for a process exit code that
    // never existed. The two BackendMissing call sites and §D4's BudgetExceeded refusal differ only in
    // status and message.
    private static RunResult NoProcessResult(JobPaths job, ResolvedRole role, RunStatus status, string? errorMessage) => new(
        SchemaVersion: "1",
        JobId: job.Id,
        Status: status,
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
        Error: errorMessage,
        Raw: null,
        Report: null,
        ReportStatus: ReportStatus.Missing,
        Warnings: []);

    // Status stays Failed here even for a termination that would otherwise map to Timeout/Cancelled
    // — this branch only runs when something *else* broke either before the process ever spawned
    // (`outcome` is then null: a bad `cwd`, `backend.Build` itself throwing) or after it already ran
    // (e.g. the after-snapshot's git process failed to spawn), so the termination enum alone would
    // misreport why the run has no diff/report.
    private static RunResult FailureResult(JobPaths job, ResolvedRole role, ProcessOutcome? outcome, Exception ex) => new(
        SchemaVersion: "1",
        JobId: job.Id,
        Status: RunStatus.Failed,
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
        ExitCode: outcome?.ExitCode ?? -1,
        LogPath: job.StdoutLog,
        DurationSeconds: outcome?.Duration.TotalSeconds ?? 0,
        Error: ex.Message,
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

    // The one funnel every result leaves through: result.json is written, and a job the ledger admitted
    // also has its reservation closed with what the run really cost. Doing both here is what keeps the
    // two from drifting apart as paths are added, the way §D4's accounting would silently leak if one
    // `return` forgot to record. `ran` is the backend process having actually run — ChargeAsync needs
    // it to tell "cost 0 because nothing happened" from "no cost reported by a backend that did run".
    private static async Task<RunResult> FinishAsync(JobPaths job, BudgetReservation? reservation, RunResult result, bool ran)
    {
        if (reservation is { } claim)
            result = await ChargeAsync(claim, result, ran);

        File.WriteAllText(job.ResultJson, JsonSerializer.Serialize(result, ClaustrumJsonContext.Default.RunResult));
        return result;
    }

    // A ledger that cannot be written must not lose a finished run (NOTES.md "Runner always yields a
    // result after the process ran"), but the cost it drops makes the tree believe it has more left
    // than it does — too consequential to swallow, so it rides out on the result as a warning. The
    // charge itself gets one too when it falls back to the granted cap: cursor and copilot report no
    // cost at all (review finding F3), and a caller reading `cost_usd: null` deserves to know the tree
    // was charged anyway.
    private static async Task<RunResult> ChargeAsync(BudgetReservation reservation, RunResult result, bool ran)
    {
        try
        {
            await reservation.CompleteAsync(result.CostUsd, ran);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return result with
            {
                Warnings = [.. result.Warnings, $"budget ledger for tree '{reservation.TreeId}' not updated with this job's cost: {ex.Message}"],
            };
        }

        if (result.CostUsd is not null || !ran)
            return result;

        return result with
        {
            Warnings = [.. result.Warnings, $"cost not reported by backend '{result.Backend}'; "
                + $"charged the granted cap {BudgetLedger.Dollars(reservation.Cap)} to tree '{reservation.TreeId}'"],
        };
    }

    private static void DeleteTempFiles(string[] tempFiles)
    {
        foreach (string tempFile in tempFiles)
            if (File.Exists(tempFile))
                File.Delete(tempFile);
    }
}
