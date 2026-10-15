using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Claustrum.Core.Backends;
using Claustrum.Core.IO;
using Claustrum.Core.Jobs;
using Claustrum.Core.Json;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Report;

namespace Claustrum.Core.Live;

// One live job's conversation (#124): turn detection, the inbox, the paused clock, the timeout warning and
// the blind rule. NOTES.md "Live sessions" has the rules and why; this class only carries them out. Stdout
// lines arrive on the pump thread and the 500 ms tick on its own task: every state change takes `gate`, and
// nothing under it blocks on the child (stdin is fed from an outbox by a single writer task). A per-turn
// protocol (opencode, NOTES.md "Live sessions on opencode") shares every rule; only how a message reaches the
// role (queued for the next process) and what ends a turn (the process exiting) differ. `resumeTurn` builds that process.
public sealed class LiveSession(
    JobPaths job,
    ILiveProtocol protocol,
    bool blind,
    int replyTimeoutSeconds,
    TimeProvider? timeProvider = null,
    TimeSpan? pollInterval = null,
    Func<string, string, ProcessSpec>? resumeTurn = null) : IProcessLive
{
    public const string BlindNotice = "This role cannot ask questions: the review is blind. Put any doubt in your findings and continue to your report.";

    public const string BlindInputNotice = "A message for this blind role was not delivered: blind roles receive no human input.";

    public const string UndeliveredNotice = "A message sent while the role was working was not delivered: the job ended before its next turn.";

    // The same cap `jobs say` puts on a message: a bigger inbox file did not come from it.
    public const int MaxInboxBytes = 1024 * 1024;

    // One rule for every case where nobody answers (no reply window, or the window passed): the roles' report text and the manual quote it.
    private const string WhenNobodyAnswers =
        "If the decision is one you can make, make it, record it in open_decisions and continue; if it is the human's, stop with status needs_input and put your questions in the report.";

    private const double WarnFraction = 0.85;

    private static readonly DateTimeOffset? ownerStartedAt = CurrentProcessStart();

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Channel<string> outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IStreamLiveProtocol? stream = protocol as IStreamLiveProtocol;
    private readonly IResumeLiveProtocol? resume = protocol as IResumeLiveProtocol;

    // Per-turn only: what the next process will be told, and the signal that wakes NextTurnAsync. A message sent while a
    // turn runs waits here for the turn to end (the process has no stdin to take it); `queuedHuman` says one of them is
    // a person's, which answers the role's question without waiting for another.
    private readonly List<string> queued = [];
    private readonly Channel<bool> wake = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
    private bool turnInFlight = true;
    private bool queuedHuman;
    private string? sessionId;
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

    // A turn carrying the report was recorded: from then on the job's outcome is that report, whatever stops the process.
    private volatile bool reported;

    public static string NoAnswerNotice(TimeSpan window) => $"No answer arrived within {Duration(window)}. {WhenNobodyAnswers}";

    public static string NoHumanNotice => $"No human is available to answer now. {WhenNobodyAnswers}";

    public static string TimeoutWarning(TimeSpan remaining)
    {
        string left = Duration(remaining);
        return $"About {left} {(left.StartsWith("1 ", StringComparison.Ordinal) ? "remains" : "remain")} before this job is stopped. Finish what you are doing and write your claustrum-report now.";
    }

    public bool ReportDelivered => reported;

    public bool PerTurn => resume is not null;

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
        if (stream?.ReadTurnEnd(line) is not { } turn)
            return;

        lock (gate)
        {
            if (state != LiveStatus.Done)
                EndTurn(turn);
        }
    }

    public async Task RunAsync(TextWriter? stdin, CancellationToken processEnded)
    {
        Task writer = stdin is null ? Task.CompletedTask : WriteLoopAsync(stdin, processEnded);
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

    // Per-turn: the process of the turn exited. What it printed is read whole, then the same rules as a stream
    // session's `result` event apply.
    public void TurnEnded(int exitCode, string stdout, string stderr)
    {
        if (resume is null)
            return;

        LiveTurn turn = resume.ReadTurn(stdout, stderr, exitCode);
        lock (gate)
        {
            turnInFlight = false;
            sessionId = turn.SessionId ?? sessionId;
            if (state != LiveStatus.Done)
                EndTurn(turn);
        }

        wake.Writer.TryWrite(true);
    }

    // Per-turn: waits until the role is to go on (a reply, a notice, a message that arrived mid-turn) and hands the
    // runner the process for it; null once the session is over. The 85% warning is checked here, because only
    // between turns is there anything to carry it: a running process has no stdin to take it.
    public async Task<ProcessSpec?> NextTurnAsync(CancellationToken cancellationToken)
    {
        string? message = null;
        string? session = null;
        while (message is null)
        {
            lock (gate)
            {
                if (state == LiveStatus.Done)
                    return null;

                if (state == LiveStatus.Running && !turnInFlight && queued.Count > 0)
                {
                    message = TakeQueued();
                    session = sessionId;
                    turnInFlight = true;
                }
            }

            if (message is null)
                await wake.Reader.ReadAsync(cancellationToken);
        }

        if (session is null || resumeTurn is null)
        {
            lock (gate)
            {
                Append(ConversationParty.Claustrum, ConversationKind.Notice, "The next turn could not be started: the backend named no session to resume");
                Finish();
            }

            return null;
        }

        // A throw here is the runner's to handle (NextTurnFailed): a turn that cannot start fails the job, it does not end it quietly.
        return resumeTurn(session, message);
    }

    // The next turn's process could not be built or started (F6): the session ends here with the reason in its conversation, and the
    // runner ends the job failed. Nothing is sent: there is no process left to take it.
    public void NextTurnFailed(string reason)
    {
        lock (gate)
        {
            if (state == LiveStatus.Done)
                return;

            Append(ConversationParty.Claustrum, ConversationKind.Notice, $"The next turn could not be started: {reason}");
            Finish();
        }
    }

    /// <summary>
    /// A line <c>claustrum-note: …</c> the role printed (#124 stage 3 review, loose end): recorded as a Message from the role with the
    /// prefix kept, as NoteRecorder does for a run with no session, so `jobs follow` shows both alike. The session owns this file's numbering,
    /// which is why the note comes through here. Nothing is written once the session is over. False when it was not recorded.
    /// </summary>
    public bool RecordNote(string text)
    {
        lock (gate)
        {
            if (state == LiveStatus.Done)
                return false;

            Append(ConversationParty.Role, ConversationKind.Message, text);
            return true;
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

        bool hasReport = ReportExtractor.Extract(turn.Text).Status != ReportStatus.Missing;
        if (hasReport || toldOnce)
        {
            Append(ConversationParty.Role, ConversationKind.Message, turn.Text);
            reported = hasReport;
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

        // The human spoke while this turn ran (per-turn only): that message is the answer, and it starts the next turn.
        if (queuedHuman)
            return;

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

    // The inbox is read and parsed before `gate` is taken and its files are deleted after it is released, so a file
    // that stalls (or a slow disk) never holds up the pump thread: a cancel or a timeout always completes.
    private void Tick()
    {
        lock (gate)
        {
            if (state == LiveStatus.Done)
                return;
        }

        List<InboxItem> inbox = ReadInbox();
        lock (gate)
        {
            if (state == LiveStatus.Done)
                return;

            foreach (InboxItem item in inbox)
            {
                if (item.Discarded is { } why)
                    Append(ConversationParty.Claustrum, ConversationKind.Notice, why);
                else if (item.Message is { Notice: true, Text.Length: > 0 } notice)
                    Append(ConversationParty.Claustrum, ConversationKind.Notice, notice.Text);
                else if (item.Message is { Text.Length: > 0 })
                    Deliver(item.Message.Text);
            }

            if (state == LiveStatus.WaitingForInput && replyTimeoutSeconds > 0
                && time.GetElapsedTime(waitingSince) >= TimeSpan.FromSeconds(replyTimeoutSeconds))
            {
                Notify(NoAnswerNotice(TimeSpan.FromSeconds(replyTimeoutSeconds)));
                toldOnce = true;
                Resume();
            }

            WarnBeforeTimeout();
        }

        // A message is recorded and queued for stdin before its file goes, so a crash between the two repeats it
        // rather than loses it.
        foreach (InboxItem item in inbox)
            TryDelete(item.File);
    }

    // Oldest first by file name (`<ticks>-<random>.json`). Files still being written never match: the writer renames
    // them into place. An item with neither message nor reason is an empty text; a file that cannot be read right now
    // (still open for writing on Windows) is left out and tried again on the next tick.
    private List<InboxItem> ReadInbox()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(job.InboxDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        Array.Sort(files, StringComparer.Ordinal);
        List<InboxItem> items = [];
        foreach (string file in files)
        {
            if (ReadInboxFile(file) is { } item)
                items.Add(item);
        }

        return items;
    }

    // Only a regular, unlinked file of at most MaxInboxBytes is read: a FIFO named `*.json` would block the read
    // for ever and `/dev/zero` behind a symlink would never end (review of #124). PlainFile opens without
    // blocking and judges the opened handle; the link check covers a regular file kept outside the store.
    private static InboxItem? ReadInboxFile(string file)
    {
        string name = Path.GetFileName(file);
        try
        {
            if (new FileInfo(file).LinkTarget is not null)
                return Discard(file, name, "it is a link");

            using FileStream? stream = PlainFile.OpenRead(file, out string? refusal);
            if (stream is null)
                return Discard(file, name, refusal ?? PlainFile.NotRegular);

            using MemoryStream bytes = new();
            byte[] chunk = new byte[16384];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                bytes.Write(chunk, 0, read);
                if (bytes.Length > MaxInboxBytes)
                    return Discard(file, name, $"it is over {MaxInboxBytes / 1024} KiB");
            }

            ReadOnlySpan<byte> json = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
            if (json.StartsWith(Encoding.UTF8.Preamble))
                json = json[Encoding.UTF8.Preamble.Length..];

            return new InboxItem(file, JsonSerializer.Deserialize(json, ClaustrumJsonContext.Default.InboxMessage), null);
        }
        catch (JsonException)
        {
            return Discard(file, name, "it is not a message");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static InboxItem Discard(string file, string name, string why) => new(file, null, $"Discarded an inbox message ({name}): {why}.");

    // A blind role is only ever told: whatever the human writes, the engine does not pass it on, because it could
    // carry the rationale the blind gate withholds (review of #124, the owner's decision). It does not count as
    // human input either: the role's one automatic answer is not reset.
    private void Deliver(string text)
    {
        if (blind)
        {
            Append(ConversationParty.Claustrum, ConversationKind.Notice, BlindInputNotice);
            return;
        }

        bool replying = state == LiveStatus.WaitingForInput;
        Append(ConversationParty.Human, replying ? ConversationKind.Reply : ConversationKind.Message, text);
        toldOnce = false;
        Send(text);

        if (replying)
            Resume();
        else
            queuedHuman = resume is not null;
    }

    // Everything waiting for the next turn, as the one message it starts with.
    private string TakeQueued()
    {
        List<string> messages = [.. queued];
        queued.Clear();
        queuedHuman = false;
        if (!warned && deadline is { Limit: { } limit } clock && clock.Active >= limit * WarnFraction)
        {
            warned = true;
            string warning = TimeoutWarning(limit - clock.Active);
            Append(ConversationParty.Claustrum, ConversationKind.Notice, warning);
            messages.Add(warning);
        }

        return string.Join("\n\n", messages);
    }

    private void WarnBeforeTimeout()
    {
        if (resume is not null || warned || state != LiveStatus.Running || deadline is not { Limit: { } limit } clock || clock.Active < limit * WarnFraction)
            return;

        warned = true;
        Notify(TimeoutWarning(limit - clock.Active));
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
        if (queuedHuman)
            Append(ConversationParty.Claustrum, ConversationKind.Notice, UndeliveredNotice);

        queued.Clear();
        queuedHuman = false;
        SetState(LiveStatus.Done);
        deadline?.Resume();
        outbox.Writer.TryComplete();
        wake.Writer.TryWrite(true);
    }

    private void Notify(string text)
    {
        Append(ConversationParty.Claustrum, ConversationKind.Notice, text);
        Send(text);
    }

    private void Send(string text)
    {
        if (resume is not null)
        {
            queued.Add(text);
            wake.Writer.TryWrite(true);
        }
        else if (stream is not null)
            outbox.Writer.TryWrite(stream.EncodeUserMessage(text));
    }

    private void SetState(LiveStatus next)
    {
        state = next;
        since = time.GetUtcNow();
        Publish();
    }

    // "1 minute" / "5 minutes"; under a minute the seconds are said, because "1 minute" for 20 seconds is a lie.
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            int seconds = Math.Max(1, (int)span.TotalSeconds);
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        int minutes = (int)Math.Round(span.TotalMinutes);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static DateTimeOffset? CurrentProcessStart()
    {
        try
        {
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

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
        LiveState snapshot = new(state, questionSeq, since, replyTimeoutSeconds, Environment.ProcessId, ownerStartedAt);
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

    private sealed record InboxItem(string File, InboxMessage? Message, string? Discarded);

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
