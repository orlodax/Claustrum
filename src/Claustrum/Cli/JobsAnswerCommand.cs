using System.CommandLine;
using Claustrum.Delegation;

namespace Claustrum.Cli;

// #124 stage 3b `claustrum jobs answer <id> (--answers <file> | <text>) [--json]`: the fallback when a role could not ask live. The job
// stopped with `needs_input` and its questions; this resumes its backend session as a new job linked to it (parent_job_id), with the
// answers as the message, and prints that job's result the way `run` does. Refuses a job that is not finished and asking, a blind
// role, a coordinate job (`coordinate --resume`) and a backend that keeps no session. See docs/MANUAL.md "When nobody can answer live".
public static class JobsAnswerCommand
{
    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "The finished job that stopped with needs_input." };
        Argument<string?> text = new("text") { Description = "The answer as free text (instead of --answers).", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> answers = new("--answers") { Description = "A JSON file mapping each question's key to its answer." };
        Option<bool> json = new("--json") { Description = "Emit exactly one RunResult JSON document on stdout (exit 7: the new job asked again)." };
        Command answer = new("answer", "Continue a finished job that stopped with needs_input: resume its session with the answers.") { id, text, answers, json };
        answer.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetRequiredValue(id),
            parseResult.GetValue(text),
            parseResult.GetValue(answers),
            parseResult.GetValue(json)));
        return answer;
    }

    private static async Task<int> ExecuteAsync(string id, string? text, string? answersPath, bool jsonMode)
    {
        if (answersPath is not null && !string.IsNullOrWhiteSpace(text))
            throw new CliUsageException("give either --answers <file> or the answer as text, not both");
        if (answersPath is null && string.IsNullOrWhiteSpace(text))
            throw new CliUsageException("no answer given: pass --answers <file> (question key -> answer) or the answer as text");

        AnswerTarget target = JobAnswering.Open(JobId.Require(id), coordinate: false);
        string brief = JobAnswering.Compose(target.Result, answersPath is null ? null : JobAnswering.ReadAnswersFile(answersPath), text);

        // The new job is a `run` like any other, in a tree too: a child resumed by its architect may ask again, and the live rules decide how.
        bool inTree = LiveRun.InTree;
        bool architectLive = LiveRun.ArchitectLive;
        (bool live, int replyTimeoutSeconds) = LiveRun.Resolve(noLive: false, replyTimeout: null, answeredByArchitect: inTree && architectLive, architectCannotAnswer: inTree && !architectLive);
        DelegateRequest request = JobAnswering.Continue(target, brief) with { Live = live, ReplyTimeoutSeconds = replyTimeoutSeconds };

        // The first Ctrl-C, SIGTERM or SIGHUP lets Runner kill the backend and write the `cancelled` receipt; a second Ctrl-C or SIGTERM exits.
        using CancelSignalHandler cancel = new();
        try
        {
            return await RunCommand.RunPreparedAsync(request, routed: inTree && architectLive, jsonMode, rawMode: false, detach: false, cancel);
        }
        catch (OperationCanceledException ex)
        {
            cancel.Settled();
            return RunCommand.Cancelled(ex);
        }
    }
}
