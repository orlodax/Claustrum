using System.CommandLine;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 stage 2 `claustrum jobs reply <id> <text> [--json]`: `jobs say`, then the wait a tree's `run` makes. The architect's loop is
// `run` -> exit 7 -> `jobs reply` -> exit 7 ... -> the child's result. Prints the next question (exit 7) or the result exactly as `run`
// would have printed it (--json: the RunResult document; the exit code is its status's). Refuses what `jobs say` refuses: an unknown,
// not-live, done, abandoned or blind job (exit 2).
public static class JobsReplyCommand
{
    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "Job id." };
        Argument<string> text = new("text") { Description = "The answer to the job's open question." };
        Option<bool> json = new("--json") { Description = "Print one document: the RunResult when the job finishes, or {status: needs_input, job_id, question: {seq, text}} when it asks again (exit 7)." };
        Command reply = new("reply", "Answer a child's question, then wait for its next question or its result (exit 7: it asked again).") { id, text, json };
        reply.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetRequiredValue(id),
            parseResult.GetRequiredValue(text),
            parseResult.GetValue(json)));
        return reply;
    }

    private static async Task<int> ExecuteAsync(string id, string text, bool jsonMode)
    {
        LiveJob job = JobRetry.Follow(LiveJob.Find(JobId.Require(id))
            ?? throw new CliUsageException($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}"));

        // Before the handler, like `run`: a refusal is a usage error, and the first signal after it is the wait's.
        SayResult sent = job.Say(text);

        using CancelSignalHandler cancel = new("stop waiting; the job keeps cancelling");
        return await DetachedRun.WaitAsync(job, sent.AnswersQuestion, jsonMode, rawMode: false, printedWarnings: [], cancel);
    }
}
