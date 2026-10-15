using System.Text.Json;
using Claustrum.Core.Model;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 stage 2: what a tree's `run` and `jobs reply` do once a job is running in its detached host: wait for it to ask or finish, and
// say which. A question ends the wait with exit 7 and the job keeps waiting for its answer; a finished job is printed exactly as `run`
// prints its own, with the exit code of its status. The first Ctrl-C, SIGTERM or SIGHUP cancels the job through its host (`jobs
// cancel`'s marker) and waits for its receipt, as an in-process `run` would have: the waiter owns the job until it returns.
// Under --json exactly one document comes out whatever happens (review of #124 stage 2, F4): the needs_input one, the job's RunResult,
// or a RunResult synthesized for a job whose own never came (abandoned: exit 6; unreadable: 1; Ctrl-C that could not cancel: 130).
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
            if (TryRequestCancel(job) is { } notCancelled)
            {
                cancel.Settled();
                JobWaitResult now = job.Observe();
                return now.State == "done" ? Report(job, now, jsonMode, rawMode, printedWarnings) : NotCancelled(job, notCancelled, jsonMode, rawMode, printedWarnings);
            }

            (seen, _) = await LiveWait.UntilAsync(job, deadline: null, answeredSeq: null, finalOnly: true, CancellationToken.None);
        }

        cancel.Settled();
        return Report(job, seen, jsonMode, rawMode, printedWarnings);
    }

    // Null when the host was asked; otherwise why it was not.
    private static string? TryRequestCancel(LiveJob job)
    {
        try
        {
            job.RequestCancel();
            return null;
        }
        catch (Exception ex) when (ex is CliUsageException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static int NotCancelled(LiveJob job, string why, bool jsonMode, bool rawMode, string[] printedWarnings)
    {
        string error = $"Ctrl-C: the cancel request for job {job.Id} could not be made, so the job was not told to stop ({why})";
        if (!jsonMode)
        {
            SafeConsole.Error($"error: {error}");
            return ExitCodes.Cancelled;
        }

        return RunCommand.Present(job.SynthesizedReceipt(RunStatus.Cancelled, error), ExitCodes.Cancelled, jsonMode, rawMode, printedWarnings);
    }

    // live.json says done before Runner has written result.json: the receipt is polled for while the host lives, and for a short grace
    // after it exits. One read synthesized a "failed" receipt for a cancelled job on macOS CI (2026-10-15).
    private static RunResult? AwaitResult(LiveJob job)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(30);
        DateTimeOffset? hostGoneAt = null;
        while (true)
        {
            if (job.ReadResult() is { } result)
                return result;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (job.ReadHost() is not { } host || !host.IsAlive())
                hostGoneAt ??= now;

            if (now >= giveUp || now - hostGoneAt > TimeSpan.FromSeconds(3))
                return null;

            Thread.Sleep(100);
        }
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
            if (!jsonMode)
            {
                LiveWait.PrintAbandoned(seen, jsonMode: false);
                return ExitCodes.Abandoned;
            }

            SafeConsole.Error(LiveJob.AbandonedMessage);
            return RunCommand.Present(job.SynthesizedReceipt(RunStatus.Failed, $"{LiveJob.AbandonedMessage} (job {job.Id})"), ExitCodes.Abandoned, jsonMode, rawMode, printedWarnings);
        }

        if (AwaitResult(job) is not { } result)
        {
            string unreadable = $"job {job.Id} ended with no readable result.json";
            if (!jsonMode)
            {
                SafeConsole.Error($"error: {unreadable}");
                return ExitCodes.BackendFailure;
            }

            return RunCommand.Present(job.SynthesizedReceipt(RunStatus.Failed, unreadable), exitOverride: null, jsonMode, rawMode, printedWarnings);
        }

        // A refusal of the host's machinery (a `.claustrum` link that leaves the repo) is exit 2 in-process too (#104).
        return RunCommand.Present(result, job.WasRefused ? ExitCodes.Usage : null, jsonMode, rawMode, printedWarnings);
    }
}
