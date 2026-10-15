using System.Text;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Backends;
using Claustrum.Core.IO;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Live;

namespace Claustrum.Delegation;

/// <summary>
/// A finished <c>needs_input</c> job, read back for a continuation: its receipt, the request it was started from and the permission
/// level it ran at (backend.json; null for a job that predates it being recorded).
/// </summary>
internal sealed record AnswerTarget(string JobId, RunResult Result, DelegateRequest Request, string? Permission = null);

// #124 stage 3b, the questions-as-data fallback (NOTES.md "Live sessions"): what `jobs answer`, `coordinate --resume` and MCP job_answer share. A role that could not
// ask live stopped with `needs_input`; this opens that job, checks it can be continued, turns the human's answers into the next
// brief and builds the request that resumes the backend's session. Every refusal is a CliUsageException (exit 2; an MCP error)
// that says which rule it is.
internal static class JobAnswering
{
    private const int MaxAnswersBytes = 1024 * 1024;

    /// <summary>
    /// Opens job <paramref name="jobId"/> for a continuation. <paramref name="coordinate"/> says which verb asks: a coordinate job is
    /// continued by `coordinate --resume`, any other by `jobs answer`, and each refuses the other's.
    /// </summary>
    public static AnswerTarget Open(string jobId, bool coordinate)
    {
        LiveJob job = LiveJob.Find(JobId.Require(jobId)) ?? throw new CliUsageException($"job '{jobId}' not found");

        // First, because a blind job never reaches needs_input: the reason to give is the role, not the status.
        if (job.IsBlind() is not false)
            throw new CliUsageException($"job '{jobId}' runs a blind role{(job.Role is { } role ? $" ({role})" : "")}, or its role cannot be read: blind roles never ask and take no human input, so there is nothing to answer");

        if (!job.HasResult)
            throw new CliUsageException($"job '{jobId}' has not finished: only a finished job that stopped with status needs_input can be answered (a running live job is answered with `jobs say`)");

        RunResult result = job.ReadResult() ?? throw new CliUsageException($"job '{jobId}' has an unreadable result.json");
        if (result.Status != RunStatus.NeedsInput)
            throw new CliUsageException($"job '{jobId}' ended {result.Status}, not needs_input: only a job whose report stopped to ask the human can be answered");

        DelegateRequest request = job.ReadResume()
            ?? throw new CliUsageException($"job '{jobId}' left no resume record (resume.json): it predates `jobs answer`, or it is still being finalised — retry in a moment, or start a new run with the answers in its brief");

        bool isCoordinateJob = request.Coordinate is not null;
        if (coordinate != isCoordinateJob)
            throw new CliUsageException(coordinate
                ? $"job '{jobId}' is not a coordinate job: continue it with `claustrum jobs answer {jobId}`"
                : $"job '{jobId}' is a coordinate job: continue it with `claustrum coordinate --resume {jobId}`");

        // A job is answered once (#124 stage 3 review F2): a second answer, or a retried tool call, would start another job on the same session.
        if (job.HasAnswered)
            throw new CliUsageException(AlreadyAnswered(jobId, job.ReadAnswered(), isCoordinateJob));

        if (!AppServices.Backends.TryGet(result.Backend, out IBackend? backend))
            throw new CliUsageException($"job '{jobId}' ran on backend '{result.Backend}', which is not registered here");
        if (!backend.CanResume)
            throw new CliUsageException($"job '{jobId}' ran on backend '{result.Backend}', which keeps no session between runs: it cannot be resumed — start a new run with the answers in its brief");
        if (result.SessionId is not { Length: > 0 })
            throw new CliUsageException($"job '{jobId}' recorded no session id, so its backend session cannot be resumed — start a new run with the answers in its brief");

        return new AnswerTarget(jobId, result, request, job.ReadPermission());
    }

    /// <summary>
    /// The request that resumes the asking job's session as a new run, linked to it: same role, cast and cwd; its branch and worktree
    /// when it was isolated (F1); and pinned to the backend, model and permission it ran as (F5), whatever the configuration says now.
    /// </summary>
    public static DelegateRequest Continue(AnswerTarget target, string brief) => target.Request with
    {
        Brief = brief,
        ResumeSession = target.Result.SessionId,
        Branch = target.Result.Branch ?? target.Request.Branch,
        WorktreeOf = HomeOf(target.Result.Worktree),
        Pin = PinOf(target),

        // The session already holds what was attached, and the file may have changed since; the cast's warnings were said once.
        AttachFiles = [],
        PreRunWarnings = null,
        ParentJobId = target.JobId,
    };

    /// <summary>What the job ran as, for the continuation's <see cref="DelegateRequest.Pin"/>.</summary>
    internal static ResumePin PinOf(AnswerTarget target) => new(target.Result.Backend, target.Result.Model, target.Permission);

    /// <summary>
    /// The job whose <c>.claustrum/worktrees/&lt;id&gt;</c> is <paramref name="worktree"/> (a receipt's <c>worktree</c>; its last segment is a
    /// job id, <c>JobWorktree.PathFor</c>), or null for a run that was not isolated. A continuation of a continuation
    /// names the first job's directory again, because that is where the second one ran.
    /// </summary>
    internal static string? HomeOf(string? worktree) =>
        worktree is not null && Path.GetFileName(Path.TrimEndingDirectorySeparator(worktree)) is { } name && JobDirectory.IsJobId(name) ? name : null;

    /// <summary>
    /// Claims <paramref name="parentJobId"/>'s one answer for the continuation <paramref name="continuationId"/>: answered.json is created
    /// exclusively, so of two answers racing the first writer wins. Null when this continuation holds the claim (also when it already did),
    /// else the refusal naming whoever does.
    /// </summary>
    public static string? TryClaim(string parentJobId, string continuationId, bool coordinate)
    {
        if (JobDirectory.Open(AppServices.Platform, parentJobId) is not { } parent)
            return null;

        AnsweredRecord record = new(continuationId, DateTimeOffset.UtcNow);
        try
        {
            if (AtomicFile.TryCreate(parent.AnsweredJson, JsonSerializer.Serialize(record, LiveJsonContext.Default.AnsweredRecord)))
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"job '{parentJobId}' could not be marked as answered ({ex.Message}); nothing was started — retry";
        }

        AnsweredRecord? holder = LiveJob.Find(parentJobId)?.ReadAnswered();
        return holder?.JobId == continuationId ? null : AlreadyAnswered(parentJobId, holder, coordinate);
    }

    /// <summary>
    /// Gives the claim back when the continuation never reached its backend (refused for its machinery, a budget, a worktree, a missing
    /// binary), so the answer was not spent and can be given again. A claim that names another job is left alone.
    /// </summary>
    public static void Release(string parentJobId, string continuationId)
    {
        if (JobDirectory.Open(AppServices.Platform, parentJobId) is not { } parent || LiveJob.Find(parentJobId)?.ReadAnswered()?.JobId != continuationId)
            return;

        try
        {
            File.Delete(parent.AnsweredJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The claim stays: a retry is refused naming a continuation that never ran, which is the safe side of "answered once".
        }
    }

    /// <summary>
    /// The refusal of a second answer: names the continuation, how it stands, and — when it stopped to ask again — that it is
    /// the one to answer now.
    /// </summary>
    public static string AlreadyAnswered(string jobId, AnsweredRecord? answered, bool coordinate)
    {
        if (answered is null)
            return $"job '{jobId}' was already answered (it has an answered.json): a second answer would start another job on the same session";

        LiveJob? continuation = LiveJob.Find(answered.JobId);
        RunResult? result = continuation?.ReadResult();
        string state = continuation is null ? "no longer in the store" : result is null ? "not finished" : $"ended {result.Status}";
        string next = result is { Status: RunStatus.NeedsInput }
            ? $"; it stopped to ask again — answer it instead: {(coordinate ? "claustrum coordinate --resume" : "claustrum jobs answer")} {answered.JobId}"
            : "";

        return $"job '{jobId}' was already answered at {answered.At:u} by job {answered.JobId} ({state}){next}";
    }

    // Longer than any chain of answers a person gives; the bound only stops a record that points back at itself.
    private const int MaxChain = 64;

    /// <summary>
    /// The first job in the chain of continuations of <paramref name="jobId"/> (its answered.json, that job's, …) that has not finished,
    /// or null. A continuation runs in the worktree of the job it continues (F1), so that worktree is not finished with while it runs.
    /// </summary>
    public static string? RunningContinuation(string jobsRoot, string jobId)
    {
        string current = jobId;
        for (int hop = 0; hop < MaxChain; hop++)
        {
            if (LiveJob.Find(current)?.ReadAnswered() is not { } answered || !JobDirectory.IsJobId(answered.JobId))
                return null;
            if (!JobDirectory.IsFinished(jobsRoot, answered.JobId))
                return answered.JobId;

            current = answered.JobId;
        }

        return null;
    }

    /// <summary>
    /// The next brief: the human's answers set against the questions the role asked. <paramref name="answers"/> maps a question's
    /// key to its answer; <paramref name="text"/> is a free-text reply. Exactly one of them is given.
    /// </summary>
    public static string Compose(RunResult asked, IReadOnlyDictionary<string, string>? answers, string? text)
    {
        bool haveAnswers = answers is not null;
        bool haveText = !string.IsNullOrWhiteSpace(text);
        if (haveAnswers == haveText)
            throw new CliUsageException("give the answers as exactly one of a JSON map of question key -> answer, or free text");

        StringBuilder brief = new("The human has answered the questions you stopped on. Continue from where you stopped; do not ask them again.\n");
        if (answers is null)
            return brief.Append("\nTheir answer:\n\n").Append(text!.Trim()).Append("\n\nFinish with your claustrum-report as before.").ToString();

        ReportQuestion[] questions = asked.Questions ?? [];
        string[] unknown = [.. answers.Keys.Where(key => questions.All(question => question.Key != key))];
        if (answers.Count == 0 || unknown.Length > 0)
            throw new CliUsageException(answers.Count == 0
                ? "no answers given"
                : $"no question has the key {string.Join(", ", unknown.Select(key => $"'{key}'"))}; the job asked: {(questions.Length == 0 ? "no keyed questions, answer with free text" : string.Join(", ", questions.Select(question => question.Key)))}");

        foreach (ReportQuestion question in questions)
        {
            brief.Append("\n- ").Append(question.Key).Append(": ").Append(question.Prompt).Append('\n');
            if (!answers.TryGetValue(question.Key, out string? answer))
            {
                brief.Append("  Answer: (not answered: decide it yourself and record the choice in open_decisions)\n");
                continue;
            }

            if (!question.AllowFreeForm && question.Options.Length > 0 && !question.Options.Contains(answer, StringComparer.Ordinal))
                throw new CliUsageException($"answer '{answer}' for '{question.Key}' is not one of its options: {string.Join(", ", question.Options)}");

            brief.Append("  Answer: ").Append(answer.Trim()).Append('\n');
        }

        return brief.Append("\nFinish with your claustrum-report as before.").ToString();
    }

    /// <summary>A JSON object of strings, key -> answer, as `--answers &lt;file&gt;` and the MCP tool's `answers` carry it.</summary>
    public static Dictionary<string, string> ReadAnswersFile(string path)
    {
        if (!File.Exists(path))
            throw new CliUsageException($"--answers file not found: {path}");
        if (new FileInfo(path).Length > MaxAnswersBytes)
            throw new CliUsageException($"--answers file {path} is over {MaxAnswersBytes / 1024} KiB");

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new CliUsageException($"--answers file {path} must hold a JSON object of question key -> answer");

            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                answers[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : throw new CliUsageException($"--answers file {path}: the answer for '{property.Name}' must be a string");
            }

            return answers;
        }
        catch (JsonException ex)
        {
            throw new CliUsageException($"--answers file {path} is not valid JSON: {ex.Message}");
        }
    }
}
