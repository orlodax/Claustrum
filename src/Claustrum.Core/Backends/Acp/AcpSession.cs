using System.Threading.Channels;
using Claustrum.Core.Jobs;
using Claustrum.Core.Live;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Acp;

// The driver of every run of an ACP backend (#130), live or not: ProcessRunner gives it the agent's stdin and every stdout line.
// RunAsync speaks the protocol (initialize, session/new or /load, the harness's config options, then one session/prompt per turn);
// OnStdoutLine, on the pump thread, completes the request a response answers and answers the agent's own requests at once. Every
// state change takes `gate`, nothing under it waits on the child, and stdin is fed from an outbox by one writer task. A live run's
// turns go through `live`, a per-turn LiveSession (NOTES.md "The ACP backend as built (2026-10-16)").
internal sealed class AcpSession(ResolvedRun run, JobPaths job, AcpHarnessSetup setup, bool loadsSessions, LiveSession? live) : IProcessLive
{
    private static readonly string clientVersion = typeof(AcpSession).Assembly.GetName().Version?.ToString(3) ?? "0";

    private readonly Lock gate = new();
    private readonly Channel<string> outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, TaskCompletionSource<AcpFrame>> pending = new(StringComparer.Ordinal);
    private readonly AcpTranscript transcript = new(loadsSessions && run.ResumeSession is { Length: > 0 } ? run.ResumeSession : null);
    private int requests;

    // Batch: the prompt was answered, so a stop after it (a timeout while the agent exits) is housekeeping, as a live report is.
    private volatile bool turnCompleted;

    public bool ReportDelivered => live?.ReportDelivered ?? turnCompleted;

    public bool PerTurn => false;

    public void Attach(PausableDeadline deadline) => live?.Attach(deadline);

    public void OnStdoutLine(string line)
    {
        AcpFrame? frame;
        TaskCompletionSource<AcpFrame>? waiter = null;
        lock (gate)
        {
            frame = transcript.Feed(line);
            if (frame is { IsResponse: true, IdText: { } id })
                pending.Remove(id, out waiter);
        }

        if (frame is null)
            return;

        if (waiter is not null)
        {
            waiter.TrySetResult(frame);
            return;
        }

        // The agent's own request, answered at once: `fs/*`, `terminal/*` and `elicitation/create` were never advertised.
        if (frame is not { IsRequest: true, Id: { } requestId, Method: { } method })
            return;

        string answer = method == "session/request_permission"
            ? AcpPermission.Answer(requestId, frame.Params, run.Role.Permission.Deny)
            : AcpWriter.MethodNotFound(requestId, method);
        outbox.Writer.TryWrite(answer);
    }

    public async Task RunAsync(TextWriter? stdin, CancellationToken processEnded)
    {
        Task writer = stdin is null ? Task.CompletedTask : WriteLoopAsync(stdin, processEnded);

        // The inbox poll and the reply clock; a per-turn LiveSession writes nothing to stdin itself.
        Task liveTicks = live?.RunAsync(null, processEnded) ?? Task.CompletedTask;
        try
        {
            if (stdin is not null)
                await ConverseAsync(processEnded);
        }
        catch (OperationCanceledException)
        {
            // The normal end of a killed run, or of an agent that exited unanswered: ProcessRunner fires the token once the process is
            // gone. No `session/cancel` is sent on a cancel: ProcessRunner kills the tree, and the token fires after the kill.
        }
        catch (Exception ex)
        {
            // A fault of this client, not the role's: ProcessRunner would carry it out of the runner, so it is said once and the
            // transcript and the exit code tell the rest.
            Say($"claustrum: the ACP client stopped ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            // Stdin closes once the outbox drains, and `opencode acp` exits 0 within 30-40 ms of that (measured 2026-10-16).
            outbox.Writer.TryComplete();
        }

        await writer;
        await liveTicks;
    }

    // ProcessRunner calls these three for a per-turn driver only; this one holds a single process for the whole run.
    public void TurnEnded(int exitCode, string stdout, string stderr)
    {
    }

    public Task<ProcessSpec?> NextTurnAsync(CancellationToken cancellationToken) => Task.FromResult<ProcessSpec?>(null);

    public void NextTurnFailed(string reason)
    {
    }

    public void Ended()
    {
        outbox.Writer.TryComplete();
        live?.Ended();
    }

    private async Task ConverseAsync(CancellationToken processEnded)
    {
        if (await HandshakeAsync(processEnded) is not { } sessionId)
            return;

        string message = run.Brief;
        while (true)
        {
            string text = message;
            await RequestAsync(AcpRequestId.Prompt, id => AcpWriter.Prompt(id, sessionId, text), processEnded);
            AcpTurn? turn;
            lock (gate)
                turn = transcript.LastTurn;

            if (live is null || turn is null)
            {
                turnCompleted = true;
                return;
            }

            // `cancelled` is not a failure: the session lives on, and a report-less turn is the role's question to its human.
            live.TurnEnded(turn.Failed ? 1 : 0, turn.Text, turn.Failure ?? "");
            if (await live.NextTurnAsync(processEnded) is not { } next)
                return;

            message = AcpLiveProtocol.Message(next);
        }
    }

    // The session to prompt, or null when the agent refused any step: then the run ends right there (stdin closes, the agent exits)
    // and Parse reads the refusal. A model the agent does not know fails here, at `set_config_option`, before anything is billed.
    private async Task<string?> HandshakeAsync(CancellationToken processEnded)
    {
        if ((await RequestAsync(AcpRequestId.Init, id => AcpWriter.Initialize(id, clientVersion), processEnded)).Error is not null)
            return null;

        string? sessionId;
        if (loadsSessions && run.ResumeSession is { Length: > 0 } resume)
        {
            AcpFrame loaded = await RequestAsync(AcpRequestId.Load, id => AcpWriter.LoadSession(id, resume, run.Cwd), processEnded);
            sessionId = loaded.Error is null ? resume : null;
        }
        else
        {
            AcpFrame opened = await RequestAsync(AcpRequestId.New, id => AcpWriter.NewSession(id, run.Cwd), processEnded);
            sessionId = opened.Error is null ? AcpJson.StringOf(opened.Result ?? default, "sessionId") : null;
        }

        if (sessionId is null)
            return null;

        foreach ((string configId, string value) in setup.ConfigOptions)
        {
            AcpFrame set = await RequestAsync(AcpRequestId.Config, id => AcpWriter.SetConfigOption(id, sessionId, configId, value), processEnded);
            if (set.Error is not null)
                return null;
        }

        return sessionId;
    }

    private async Task<AcpFrame> RequestAsync(string tag, Func<string, string> encode, CancellationToken processEnded)
    {
        // Completed off the pump thread: a continuation run inline there would hold the pump while the turn loop goes on.
        TaskCompletionSource<AcpFrame> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        lock (gate)
        {
            id = AcpRequestId.Make(tag, ++requests);
            pending[id] = reply;
        }

        if (!outbox.Writer.TryWrite(encode(id)))
            throw new OperationCanceledException("the ACP session is closed", processEnded);

        return await reply.Task.WaitAsync(processEnded);
    }

    // The only code that touches stdin: frames in order, each also appended to the client's half of the transcript, then the close
    // that ends the session (LiveSession.WriteLoopAsync's shape).
    private async Task WriteLoopAsync(TextWriter stdin, CancellationToken processEnded)
    {
        StreamWriter? clientLog = OpenClientLog();
        try
        {
            await foreach (string line in outbox.Reader.ReadAllAsync(CancellationToken.None))
            {
                await stdin.WriteAsync($"{line}\n".AsMemory(), processEnded);
                await stdin.FlushAsync(processEnded);
                Guarded(() => clientLog?.WriteLine(line));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A child that is gone takes no more frames; the process outcome says why.
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

            Guarded(() => clientLog?.Dispose());
        }
    }

    // The client's log costs the record, never the run: a job directory that stops taking writes is not the agent's failure.
    private StreamWriter? OpenClientLog()
    {
        try
        {
            return new StreamWriter(job.AcpClientLog, append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    private static void Say(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (IOException)
        {
            // stderr is gone as well; nothing left to tell.
        }
    }
}
