using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Report;

namespace Claustrum.Core.Live;

// One live job's conversation (#124): turn detection, the inbox, the paused clock, the timeout warning and
// the blind rule. NOTES.md "Live sessions" has the rules and why; this class only carries them out. Stdout
// lines arrive on the pump thread and the 500 ms tick on its own task: every state change takes `gate`, and
// nothing under it blocks on the child (stdin is fed from an outbox by a single writer task).
public sealed class LiveSession(JobPaths job, ILiveProtocol protocol, bool blind, int replyTimeoutSeconds, TimeProvider? timeProvider = null, TimeSpan? pollInterval = null) : IProcessLive
{
    public const string BlindNotice = "This role cannot ask questions: the review is blind. Put any doubt in your findings and continue to your report.";

    private const string DecideAlone = "Decide yourself, record the choice and why in open_decisions, and continue.";

    private const double WarnFraction = 0.85;

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Channel<string> outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private PausableDeadline? deadline;
    private LiveStatus state = LiveStatus.Running;
    private DateTimeOffset since;
    private int? questionSeq;
    private long waitingSince;
    private int seq;

    // The role was told once that nobody answers (or that it is blind); a second turn without a report after
    // that ends the session instead of looping on a role that never writes one. A human message clears it.
    private bool toldOnce;
    private bool warned;

    // Set once the session is over: messages still unsent are dropped, or the child would start a turn after
    // its report and the last `result` in the stream would no longer be the one that carries it.
    private volatile bool closing;

    public static string NoAnswerNotice(int minutes) => $"No answer arrived within {minutes} minutes. {DecideAlone}";

    public static string NoHumanNotice => $"No human is available to answer. {DecideAlone}";

    public static string TimeoutWarning(int minutes) =>
        $"About {minutes} minutes remain before this job is stopped. Finish what you are doing and write your claustrum-report now.";

    public int Entries
    {
        get
        {
            lock (gate)
                return seq;
        }
    }

    public void Attach(PausableDeadline deadline)
    {
        lock (gate)
        {
            this.deadline = deadline;
            since = time.GetUtcNow();
            Guarded(() =>
            {
                Directory.CreateDirectory(job.InboxDirectory);
                File.WriteAllBytes(job.ConversationJsonl, []);
            });
            Publish();
        }
    }

    public void OnStdoutLine(string line)
    {
        if (protocol.ReadTurnEnd(line) is not { } turn)
            return;

        lock (gate)
        {
            if (state != LiveStatus.Done)
                EndTurn(turn);
        }
    }

    public async Task RunAsync(TextWriter stdin, CancellationToken processEnded)
    {
        Task writer = WriteLoopAsync(stdin, processEnded);
        using PeriodicTimer timer = new(pollInterval ?? TimeSpan.FromMilliseconds(500), time);
        try
        {
            while (await timer.WaitForNextTickAsync(processEnded))
                Tick();
        }
        catch (OperationCanceledException)
        {
        }

        outbox.Writer.TryComplete();
        await writer;
    }

    public void Ended()
    {
        lock (gate)
        {
            if (state != LiveStatus.Done)
                Finish();

            outbox.Writer.TryComplete();
        }
    }

    // A turn ended: a report ends the session, a turn the backend failed ends it too, and anything else is
    // the role talking to its human. Blind roles tell and never wait; with no reply window nobody is asked.
    private void EndTurn(LiveTurn turn)
    {
        if (turn.IsError)
        {
            Append(ConversationParty.Claustrum, ConversationKind.Notice, $"The role's session ended with an error: {turn.Text}");
            Finish();
            return;
        }

        if (ReportExtractor.Extract(turn.Text).Status != ReportStatus.Missing || toldOnce)
        {
            Append(ConversationParty.Role, ConversationKind.Message, turn.Text);
            Finish();
            return;
        }

        if (blind)
        {
            Append(ConversationParty.Role, ConversationKind.Message, turn.Text);
            Notify(BlindNotice);
            toldOnce = true;
            return;
        }

        int question = Append(ConversationParty.Role, ConversationKind.Question, turn.Text);
        if (replyTimeoutSeconds <= 0)
        {
            Notify(NoHumanNotice);
            toldOnce = true;
            return;
        }

        questionSeq = question;
        waitingSince = time.GetTimestamp();
        SetState(LiveStatus.WaitingForInput);
        deadline?.Pause();
    }

    private void Tick()
    {
        lock (gate)
        {
            if (state == LiveStatus.Done)
                return;

            DrainInbox();

            if (state == LiveStatus.WaitingForInput && replyTimeoutSeconds > 0
                && time.GetElapsedTime(waitingSince) >= TimeSpan.FromSeconds(replyTimeoutSeconds))
            {
                Notify(NoAnswerNotice(Minutes(TimeSpan.FromSeconds(replyTimeoutSeconds))));
                toldOnce = true;
                Resume();
            }

            WarnBeforeTimeout();
        }
    }

    // Oldest first by file name (`<ticks>-<random>.json`). A message is recorded and queued for stdin before its
    // file is deleted, so a crash between the two repeats it rather than loses it. Files still being written
    // never match: the writer renames them into place.
    private void DrainInbox()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(job.InboxDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            InboxMessage? message;
            try
            {
                message = JsonSerializer.Deserialize(File.ReadAllText(file), ClaustrumJsonContext.Default.InboxMessage);
            }
            catch (JsonException)
            {
                Append(ConversationParty.Claustrum, ConversationKind.Notice, $"Discarded an unreadable inbox message ({Path.GetFileName(file)}).");
                TryDelete(file);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (message is { Text.Length: > 0 })
                Deliver(message.Text);

            TryDelete(file);
        }
    }

    private void Deliver(string text)
    {
        bool replying = state == LiveStatus.WaitingForInput;
        Append(ConversationParty.Human, replying ? ConversationKind.Reply : ConversationKind.Message, text);
        toldOnce = false;
        Send(text);

        if (replying)
            Resume();
    }

    private void WarnBeforeTimeout()
    {
        if (warned || state != LiveStatus.Running || deadline is not { Limit: { } limit } clock || clock.Active < limit * WarnFraction)
            return;

        warned = true;
        Notify(TimeoutWarning(Minutes(limit - clock.Active)));
    }

    // The role is working again: the question is closed and the clock runs.
    private void Resume()
    {
        questionSeq = null;
        SetState(LiveStatus.Running);
        deadline?.Resume();
    }

    private void Finish()
    {
        closing = true;
        questionSeq = null;
        SetState(LiveStatus.Done);
        deadline?.Resume();
        outbox.Writer.TryComplete();
    }

    private void Notify(string text)
    {
        Append(ConversationParty.Claustrum, ConversationKind.Notice, text);
        Send(text);
    }

    private void Send(string text) => outbox.Writer.TryWrite(protocol.EncodeUserMessage(text));

    private void SetState(LiveStatus next)
    {
        state = next;
        since = time.GetUtcNow();
        Publish();
    }

    private static int Minutes(TimeSpan span) => Math.Max(1, (int)Math.Round(span.TotalMinutes));

    // One JSON line in one write, so a reader following the file never sees half of an entry it can parse.
    private int Append(ConversationParty from, ConversationKind kind, string text)
    {
        seq++;
        ConversationEntry entry = new(seq, time.GetUtcNow(), from, kind, text);
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, ClaustrumJsonContext.Default.ConversationEntry) + "\n");
        Guarded(() =>
        {
            using FileStream stream = new(job.ConversationJsonl, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            stream.Write(line);
        });

        return seq;
    }

    // live.json is replaced whole (temp file, then rename): a reader sees the old state or the new one.
    // Windows refuses the rename while a reader holds the file, so it is tried a few times.
    private void Publish()
    {
        LiveState snapshot = new(state, questionSeq, since, replyTimeoutSeconds);
        string json = JsonSerializer.Serialize(snapshot, ClaustrumJsonContext.Default.LiveState);
        string temp = job.LiveJson + ".tmp";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, job.LiveJson, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }

    // A job directory that stops taking writes costs the conversation record, never the run: the process is
    // already started when any of this runs, and a throw on the pump thread would end it.
    private static void Guarded(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string file) => Guarded(() => File.Delete(file));

    // The only code that touches stdin after the brief: messages in order, then the close that tells the
    // child the conversation is over (claude exits 0 on a closed stdin).
    private async Task WriteLoopAsync(TextWriter stdin, CancellationToken processEnded)
    {
        try
        {
            await foreach (string line in outbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (closing)
                    continue;

                await stdin.WriteAsync($"{line}\n".AsMemory(), processEnded);
                await stdin.FlushAsync(processEnded);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A child that is gone takes no more messages; the process outcome says why.
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }
    }
}
