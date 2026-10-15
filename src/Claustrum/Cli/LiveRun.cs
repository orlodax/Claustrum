using System.CommandLine;
using Claustrum.Core.Jobs;
using Claustrum.Core.Live;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Live;

namespace Claustrum.Cli;

// #124: what `run` and `coordinate` share about live sessions: the two flags, the TTY rule that picks the reply timeout,
// and the loop that shows the conversation and asks the human while the job runs.
internal static class LiveRun
{
    private const int PollIntervalMs = 500;

    public static Option<int?> ReplyTimeoutOption() => new("--reply-timeout")
    {
        Description = $"Seconds an open question waits for a human reply before the role is told no human is available (default {DelegateEngine.DefaultReplyTimeoutSeconds} on a terminal or as a child in a job tree whose architect is live, 0 otherwise: the role is told at once nobody can answer).",
    };

    public static Option<bool> NoLiveOption() => new("--no-live") { Description = "Run as before the live channel: no conversation, so the role cannot ask the human anything." };

    /// <summary>Whether this process is a member of a job tree (CLAUSTRUM_PARENT_JOB): a child of a coordinate architect.</summary>
    public static bool InTree => BudgetLedger.TreeIdFor(AppServices.Platform) is not null;

    /// <summary>
    /// Whether the tree's architect said it holds a live session (CLAUSTRUM_ARCHITECT_LIVE=1, set by `coordinate`), so a child's question can reach
    /// it. Read as given, never inferred: a tree id exported by hand, or an architect run with <c>--no-live</c>, leaves no one to answer (review of #124 stage 2, F2).
    /// </summary>
    public static bool ArchitectLive => AppServices.Platform.GetEnvironmentVariable(BudgetLedger.ArchitectLiveVariable)?.Trim() == "1";

    /// <summary>
    /// The live options for a run: live unless <c>--no-live</c>, with the reply timeout of <c>--reply-timeout</c>, else
    /// 1800 s when someone can answer (stdin and stderr are terminals, or <paramref name="answeredByArchitect"/>: a tree's
    /// child, whose architect replies with `jobs reply`) and 0 when not (the engine tells the role at once).
    /// <paramref name="architectCannotAnswer"/> is a child of a tree whose architect is not live: a terminal there is the
    /// architect's tool shell, and nobody reads it.
    /// </summary>
    public static (bool Live, int ReplyTimeoutSeconds) Resolve(bool noLive, int? replyTimeout, bool answeredByArchitect = false, bool architectCannotAnswer = false)
    {
        if (replyTimeout is < 0)
            throw new CliUsageException("--reply-timeout must be zero or more seconds");

        if (!noLive)
        {
            bool someoneAnswers = answeredByArchitect || (!architectCannotAnswer && TerminalInput.IsInteractive);
            return (true, replyTimeout ?? (someoneAnswers ? DelegateEngine.DefaultReplyTimeoutSeconds : 0));
        }

        return replyTimeout is null ? (false, 0) : throw new CliUsageException("--reply-timeout has no meaning with --no-live");
    }

    /// <summary>Whether this run shows the conversation and asks on the terminal: a live run with someone at it.</summary>
    public static bool Prompts(PreparedDelegation prepared) => prepared.Options.Live && TerminalInput.IsInteractive;

    /// <summary>
    /// Awaits <paramref name="run"/> while printing its job's conversation and asking each open question on the terminal. The
    /// run is the only thing awaited without a race: a typed line never delays a cancel, and a failure here only ends the prompting.
    /// </summary>
    public static async Task<RunResult> AnswerWhileAsync(Task<RunResult> run, JobPaths jobPaths, Func<IPromptTerminal?>? openTerminal = null)
    {
        LiveJob job = LiveJob.At(jobPaths);
        ConversationPrinter printer = new();
        QuestionPrompter prompter = new(job, openTerminal);
        try
        {
            while (!run.IsCompleted)
            {
                IReadOnlyList<ConversationEntry> entries = job.ReadConversation();
                printer.Print(entries, prefix: null);
                prompter.Poll(job.ReadState(), entries);

                List<Task> wake = [run, Task.Delay(PollIntervalMs)];
                if (prompter.Pending is { } pending)
                    wake.Add(pending);

                await Task.WhenAny(wake);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SafeConsole.Error($"warning: live prompting stopped: {e.Message}");
        }

        prompter.Abandon();
        printer.Print(job.ReadConversation(), prefix: null);
        return await run;
    }
}
