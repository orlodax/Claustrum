using System.Text;
using System.Text.Json;
using Claustrum.Cli;
using Claustrum.Core.Backends;
using Claustrum.Core.Model;
using Claustrum.Live;

namespace Claustrum.Delegation;

/// <summary>A finished <c>needs_input</c> job, read back for a continuation: its receipt and the request it was started from.</summary>
internal sealed record AnswerTarget(string JobId, RunResult Result, DelegateRequest Request);

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

        if (!AppServices.Backends.TryGet(result.Backend, out IBackend? backend))
            throw new CliUsageException($"job '{jobId}' ran on backend '{result.Backend}', which is not registered here");
        if (!backend.CanResume)
            throw new CliUsageException($"job '{jobId}' ran on backend '{result.Backend}', which keeps no session between runs: it cannot be resumed — start a new run with the answers in its brief");
        if (result.SessionId is not { Length: > 0 })
            throw new CliUsageException($"job '{jobId}' recorded no session id, so its backend session cannot be resumed — start a new run with the answers in its brief");

        return new AnswerTarget(jobId, result, request);
    }

    /// <summary>The request that resumes the asking job's session as a new run, linked to it: same role, cast, backend, model, cwd, and its branch when it was isolated.</summary>
    public static DelegateRequest Continue(AnswerTarget target, string brief) => target.Request with
    {
        Brief = brief,
        ResumeSession = target.Result.SessionId,
        Branch = target.Result.Branch ?? target.Request.Branch,

        // The session already holds what was attached, and the file may have changed since; the cast's warnings were said once.
        AttachFiles = [],
        PreRunWarnings = null,
        ParentJobId = target.JobId,
    };

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
