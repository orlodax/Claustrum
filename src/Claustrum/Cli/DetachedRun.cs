using System.Text.Json;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 stage 2: what a tree's `run` and `jobs reply` do once a job is running in its detached host: wait for it to ask or finish, and
// say which. A question ends the wait with exit 7 and the job keeps waiting for its answer; a finished job is printed exactly as `run`
// prints its own, with the exit code of its status. The first Ctrl-C, SIGTERM or SIGHUP cancels the job through its host (`jobs
// cancel`'s marker) and waits for its receipt, as an in-process `run` would have: the waiter owns the job until it returns.
internal static class DetachedRun
{
    public static async Task<int> WaitAsync(LiveJob job, int? answeredSeq, bool jsonMode, bool rawMode, string[] printedWarnings, CancelSignalHandler cancel)
    {
        JobWaitResult seen;
        try
        {
            (seen, _) = await LiveWait.UntilAsync(job, deadline: null, answeredSeq, finalOnly: false, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // A job with no host (or one that is already done or gone) is not this process's to cancel: it is left as it was.
            try
            {
                job.RequestCancel();
            }
            catch (CliUsageException)
            {
                cancel.Settled();
                JobWaitResult now = job.Observe();
                return now.State == "done" ? Report(job, now, jsonMode, rawMode, printedWarnings) : ExitCodes.Cancelled;
            }

            (seen, _) = await LiveWait.UntilAsync(job, deadline: null, answeredSeq: null, finalOnly: true, CancellationToken.None);
        }

        cancel.Settled();
        return Report(job, seen, jsonMode, rawMode, printedWarnings);
    }

    private static int Report(LiveJob job, JobWaitResult seen, bool jsonMode, bool rawMode, string[] printedWarnings)
    {
        if (seen is { State: "waiting_for_input", Question: { } question })
        {
            if (jsonMode)
            {
                SafeConsole.Out(JsonSerializer.Serialize(NeedsInputResult.For(job.Id, question), LiveJsonContext.Default.NeedsInputResult));
            }
            else
            {
                SafeConsole.Out($"needs input: {job.Id}");
                foreach (string line in ConversationLines.Render("?", question.Text))
                    SafeConsole.Out(line);
            }

            return ExitCodes.NeedsInput;
        }

        if (seen.State == "abandoned")
        {
            LiveWait.PrintAbandoned(seen, jsonMode);
            return ExitCodes.Abandoned;
        }

        if (job.ReadResult() is not { } result)
        {
            SafeConsole.Error($"error: job {job.Id} ended with no readable result.json");
            return ExitCodes.BackendFailure;
        }

        return RunCommand.Present(result, refusedExit: null, jsonMode, rawMode, printedWarnings);
    }
}
