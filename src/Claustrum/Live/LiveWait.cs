using System.Text.Json;
using Claustrum.Cli;

namespace Claustrum.Live;

// #124: the one wait every front door shares: `jobs wait`, a tree's `run`, `jobs reply`. Polls LiveJob.Observe until the job
// asks a question, finishes or is abandoned.
internal static class LiveWait
{
    public const int PollIntervalMs = 500;

    // A job whose live.json says done, or whose result.json is cut short, gets this many polls (~10 s) to show a readable
    // receipt before it is reported without one: the process that wrote the one may have died before the other.
    private const int ReceiptPolls = 20;

    /// <summary>
    /// Where the job stands once it settles, and whether <paramref name="deadline"/> passed first (then it is the last thing seen).
    /// <paramref name="answeredSeq"/> is the question a reply was just queued for: still showing as open, it is the question
    /// that reply answers, not a new one. <paramref name="finalOnly"/> waits for the end (a cancel in flight) and ignores questions.
    /// </summary>
    public static async Task<(JobWaitResult Seen, bool TimedOut)> UntilAsync(
        LiveJob job, DateTime? deadline, int? answeredSeq, bool finalOnly, CancellationToken cancellationToken)
    {
        int receiptPolls = 0;
        while (true)
        {
            JobWaitResult seen = job.Observe();
            bool askedAgain = seen.State == "waiting_for_input" && !finalOnly && seen.Question?.Seq != answeredSeq;
            if (seen.State == "abandoned" || askedAgain || (seen.State == "done" && (seen.ResultStatus is not null || ++receiptPolls >= ReceiptPolls)))
                return (seen, false);

            TimeSpan left = deadline is { } due ? due - DateTime.UtcNow : TimeSpan.MaxValue;
            if (left <= TimeSpan.Zero)
                return (seen, true);

            TimeSpan step = TimeSpan.FromMilliseconds(PollIntervalMs);
            await Task.Delay(left < step ? left : step, cancellationToken);
        }
    }

    /// <summary>Says an abandoned job is: on stdout in plain mode, where the answer is; under --json stdout stays the one document.</summary>
    public static void PrintAbandoned(JobWaitResult seen, bool jsonMode)
    {
        if (jsonMode)
        {
            SafeConsole.Out(JsonSerializer.Serialize(seen, LiveJsonContext.Default.JobWaitResult));
            SafeConsole.Error(LiveJob.AbandonedMessage);
        }
        else
        {
            SafeConsole.Out(LiveJob.AbandonedMessage);
        }
    }
}
