using System.CommandLine;
using System.Text.Json;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 stage 2 `claustrum jobs cancel <id>`: ends a detached job. The host polls for the marker this leaves and ends the job `cancelled`
// with its receipt, as Ctrl-C ends an in-process run; this waits for that receipt and prints its summary line. Exit 0 once the job has
// ended, 2 when it is unknown, not detached, done or abandoned (nothing to cancel), 4 when the host has not finished within two minutes,
// 6 when the host died without a receipt. Given a job its cast's fallback took over (#126), it cancels the retry, which runs in the same host.
public static class JobsCancelCommand
{
    private static readonly TimeSpan receiptWait = TimeSpan.FromMinutes(2);

    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "Job id." };
        Command cancel = new("cancel", "Cancel a detached job (`run --detach`, a tree's child): its host ends it `cancelled`, with its receipt.") { id };
        cancel.SetAction(async parseResult => await ExecuteAsync(parseResult.GetRequiredValue(id)));
        return cancel;
    }

    private static async Task<int> ExecuteAsync(string id)
    {
        LiveJob job = JobRetry.Follow(LiveJob.Find(JobId.Require(id))
            ?? throw new CliUsageException($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}"));

        job.RequestCancel();
        SafeConsole.Error($"cancelling {job.Id}…");

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        try
        {
            (JobWaitResult seen, bool timedOut) = await LiveWait.UntilAsync(job, DateTime.UtcNow + receiptWait, answeredSeq: null, finalOnly: true, cts.Token);
            if (seen.State == "abandoned")
            {
                LiveWait.PrintAbandoned(seen, jsonMode: false);
                return ExitCodes.Abandoned;
            }

            if (timedOut)
            {
                SafeConsole.Error($"job {job.Id} has not ended after {receiptWait.TotalSeconds:0}s; it is still cancelling (see `claustrum jobs wait {job.Id}`)");
                return ExitCodes.Timeout;
            }

            using JsonDocument? receipt = job.ReadResultDocument(out _);
            SafeConsole.Out(receipt is null ? "━━━ incomplete (no readable result.json)" : JobsFollowCommand.FormatSummary(receipt.RootElement));
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
    }
}
