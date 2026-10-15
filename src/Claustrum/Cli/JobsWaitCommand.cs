using System.CommandLine;
using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Jobs;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124 `claustrum jobs wait <id> [--timeout <s>] [--json]`: blocks until the job asks a question or finishes, and says which.
// The primitive a coordinating architect polls with: stdout is the answer (a question as `? text` and its seq, or the
// summary line `jobs follow` ends on; one JSON document under --json), exit 0 for either, 4 on --timeout, 6 when the
// job is abandoned (its claustrum process is gone and left no result.json), 130 on Ctrl-C.
public static class JobsWaitCommand
{
    public static Command Build()
    {
        Argument<string> id = new("id") { Description = "Job id." };
        Option<int?> timeout = new("--timeout") { Description = "Give up after this many seconds (exit 4)." };
        Option<bool> json = new("--json") { Description = "Print one document: {state: running|waiting_for_input|done|abandoned, question: {seq, text} | null, result_status | null}." };
        Command wait = new("wait", "Block until a job asks a question or finishes (poll-based, ~500 ms).") { id, timeout, json };
        wait.SetAction(async parseResult => await ExecuteAsync(
            parseResult.GetRequiredValue(id),
            parseResult.GetValue(timeout),
            parseResult.GetValue(json)));
        return wait;
    }

    private static async Task<int> ExecuteAsync(string id, int? timeoutSeconds, bool jsonMode)
    {
        if (timeoutSeconds is <= 0)
            throw new CliUsageException("--timeout must be greater than zero");

        LiveJob job = LiveJob.Find(JobId.Require(id))
            ?? throw new CliUsageException($"job '{id}' not found under {JobDirectory.ResolveRoot(AppServices.Platform)}");

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        DateTime? deadline = timeoutSeconds is { } seconds ? DateTime.UtcNow.AddSeconds(seconds) : null;
        try
        {
            (JobWaitResult seen, bool timedOut) = await LiveWait.UntilAsync(job, deadline, answeredSeq: null, finalOnly: false, cts.Token);
            if (seen.State == "abandoned")
            {
                LiveWait.PrintAbandoned(seen, jsonMode);
                return ExitCodes.Abandoned;
            }

            if (timedOut)
            {
                SafeConsole.Error($"timed out after {timeoutSeconds}s waiting for job {StreamText.Sanitize(id)} (state: {seen.State})");
                if (jsonMode)
                    PrintJson(seen);

                return ExitCodes.Timeout;
            }

            Report(job, seen, jsonMode);
            return ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Cancelled;
        }
    }

    private static void Report(LiveJob job, JobWaitResult seen, bool jsonMode)
    {
        if (jsonMode)
        {
            PrintJson(seen);
            return;
        }

        if (seen.Question is { } question)
        {
            foreach (string line in ConversationLines.Render("?", question.Text))
                SafeConsole.Out(line);

            SafeConsole.Out($"(question {question.Seq})");
            return;
        }

        using JsonDocument? receipt = job.ReadResultDocument(out _);
        SafeConsole.Out(receipt is null ? "━━━ incomplete (no readable result.json)" : JobsFollowCommand.FormatSummary(receipt.RootElement));
    }

    private static void PrintJson(JobWaitResult seen) =>
        SafeConsole.Out(JsonSerializer.Serialize(seen, LiveJsonContext.Default.JobWaitResult));
}
