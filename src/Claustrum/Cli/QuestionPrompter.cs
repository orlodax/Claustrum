using Claustrum.Core.Live;
using Claustrum.Live;

namespace Claustrum.Cli;

// Asks a human on the terminal what a live role asked (#124): `jobs follow <id>`, and `run`/`coordinate` while their job
// runs. Polled, never blocking: Poll shows `reply> ` once per question and starts one terminal read, and the finished
// line goes out as `jobs say` would send it. An empty line means don't answer now, and the question is not asked again.
// A reply answers only what was on screen: type-ahead is discarded before each prompt. A blind role is never asked
// (nothing is delivered to it), and an abandoned job stops the prompting. The question text itself is printed by
// ConversationPrinter, so the prompt always follows it.
internal sealed class QuestionPrompter(LiveJob job)
{
    private TerminalInput? terminal;
    private Task<string?>? read;
    private int readSeq = -1;
    private int askedSeq = -1;
    private bool? blind;
    private bool disabled;

    /// <summary>The terminal read in flight, for a wait loop to wake on; null when no prompt is up.</summary>
    public Task? Pending => read;

    public void Poll(LiveState? state, IReadOnlyList<ConversationEntry> entries)
    {
        if (disabled)
            return;

        ConversationEntry? open = LiveJob.OpenQuestion(state, entries);
        if (read is { IsCompleted: true })
            Finish(open);

        // Saying so is the caller's: `jobs follow` prints it once, and a run's own job cannot be abandoned.
        if (job.IsAbandoned(state))
        {
            disabled = true;
            return;
        }

        // An answer another front door already queued is on its way: asking again would only collect a second one.
        if (open is null || open.Seq == askedSeq || job.HasQueuedMessage())
            return;

        // Unknown (no request.json yet) counts as blind and is asked again on the next poll.
        blind ??= job.IsBlind();
        if (blind is not false)
            return;

        askedSeq = open.Seq;
        terminal ??= TerminalInput.TryOpen();
        if (terminal is null)
        {
            disabled = true;
            SafeConsole.Error($"(no terminal to ask on: answer with `claustrum jobs say {job.Id} \"…\"`)");
            return;
        }

        // A read left over from an earlier question is reused: a second one on the same terminal would race it for the line.
        readSeq = open.Seq;
        if (read is null)
            terminal.DiscardPending();

        SafeConsole.Prompt("reply> ");
        read ??= terminal.ReadLineAsync();
    }

    /// <summary>Ends the prompt line a run's end (or a Ctrl-C) leaves hanging; the read itself dies with the process.</summary>
    public void Abandon()
    {
        if (read is { IsCompleted: false })
            SafeConsole.Error("");
    }

    private void Finish(ConversationEntry? open)
    {
        string? line = null;
        try
        {
            line = read?.GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // A terminal that cannot be read ends the prompting, like end of input.
        }

        read = null;
        if (line is null)
        {
            disabled = true;
            SafeConsole.Error("(the terminal closed: no more prompts)");
            return;
        }

        if (line.Trim().Length == 0)
        {
            SafeConsole.Error($"not answered; reply later with: claustrum jobs say {job.Id} \"<answer>\"");
            return;
        }

        if (open?.Seq != readSeq)
        {
            SafeConsole.Error($"(not sent: question {readSeq} is no longer open; `claustrum jobs say {job.Id} \"…\"` reaches the role)");
            return;
        }

        try
        {
            job.Say(line);
        }
        catch (Exception e) when (e is CliUsageException or IOException or UnauthorizedAccessException)
        {
            SafeConsole.Error($"warning: the reply was not sent: {e.Message}");
        }
    }
}
