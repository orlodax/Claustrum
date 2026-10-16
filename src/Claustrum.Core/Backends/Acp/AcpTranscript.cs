using System.Text;
using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// The one reader of an ACP run's agent→client half (stdout.log): Parse reads a finished run with it, AcpSession a running one line by
// line, and the post-run hook re-reads a run's stream to tell a handshake refusal. Every shape here was measured on opencode 2.0.30
// (NOTES.md "ACP backend: cost on a paid model, measured before the parser was designed"). A malformed line is skipped, never thrown on.
internal sealed class AcpTranscript(string? sessionId = null)
{
    private readonly string? resumeSession = sessionId;
    private readonly StringBuilder pending = new();
    private string? pendingMessageId;
    private bool pricedSinceAnswer;

    /// <summary>
    /// The run's own session: a <c>session/new</c> answer's, else the first update's; updates naming another are not read. Once
    /// <see cref="Loaded"/>, the id the transcript was built with (the resume id) stays, whatever the updates say.
    /// </summary>
    public string? SessionId { get; private set; } = sessionId;

    public List<AcpTurn> Turns { get; } = [];

    public AcpTurn? LastTurn => Turns.Count > 0 ? Turns[^1] : null;

    /// <summary>The text of a turn not yet answered (a run cut off mid-turn).</summary>
    public string PendingText => pending.ToString();

    /// <summary>The last <c>usage_update</c> cost in USD: the session's running total on opencode, not this run's share.</summary>
    public decimal? RunningCostUsd { get; private set; }

    /// <summary>A <c>session/load</c> succeeded: the session, and its running total, began before this run.</summary>
    public bool Loaded { get; private set; }

    /// <summary>A <c>session/new</c> or <c>session/load</c> succeeded: only from then on does a turn update belong to this run.</summary>
    public bool Opened { get; private set; }

    /// <summary>The first error answering <c>initialize</c>, <c>session/new</c>, <c>session/load</c> or a config option: nothing was prompted.</summary>
    public AcpError? HandshakeError { get; private set; }

    public string? HandshakeTag { get; private set; }

    /// <summary>Each tool call's last status (<c>pending</c>, <c>in_progress</c>, <c>completed</c>, <c>failed</c>); <c>pending</c> until one is said.</summary>
    public Dictionary<string, string> ToolCallStatus { get; } = [];

    /// <summary>The agent spoke, thought, planned, called a tool or reported usage after the last answered prompt: a turn was under way.</summary>
    public bool ActiveAfterLastTurn { get; private set; }

    public bool AnyPromptAnswered => Turns.Count > 0;

    /// <summary>A <c>usage_update</c> total came in the last answered turn; one that failed part-way may have none.</summary>
    public bool LastTurnPriced { get; private set; }

    /// <summary>
    /// The running total is the run's whole cost: a prompt was answered, its turn priced (else the figure is an earlier turn's), the
    /// agent sent nothing of a turn after the answer, and the session did not begin before this run (no <c>session/load</c>).
    /// </summary>
    public bool Settled => AnyPromptAnswered && LastTurnPriced && !ActiveAfterLastTurn && !Loaded;

    public bool SawFrame { get; private set; }

    public static AcpTranscript Read(string stdout)
    {
        AcpTranscript transcript = new();
        foreach (string line in stdout.Split('\n'))
            transcript.Feed(line.Trim());

        return transcript;
    }

    /// <summary>Reads one line; the frame comes back so a caller can act on a request or a response, null for a line that is none.</summary>
    public AcpFrame? Feed(string line)
    {
        if (AcpFrame.TryParse(line) is not { } frame)
            return null;

        SawFrame = true;
        if (frame.IsResponse)
            OnResponse(frame);
        else if (frame is { IsNotification: true, Method: "session/update", Params: { } parameters })
            OnUpdate(parameters);

        return frame;
    }

    private void OnResponse(AcpFrame frame)
    {
        string? answers = frame.Answers;
        AcpError? error = frame.Error is { } errorElement ? ErrorOf(errorElement) : null;
        if (answers == AcpRequestId.Prompt)
        {
            JsonElement result = frame.Result ?? default;
            Turns.Add(new AcpTurn(pending.ToString(), AcpJson.StringOf(result, "stopReason"), AcpJson.ObjectOf(result, "usage"), error, frame.Result ?? frame.Error));
            pending.Clear();
            ActiveAfterLastTurn = false;
            (LastTurnPriced, pricedSinceAnswer) = (pricedSinceAnswer, false);
            return;
        }

        // The handshake, before any prompt: nothing said until then belongs to a turn of this run (a `session/load` replays the whole
        // history, chunks and tool calls, before it answers: measured 2026-10-16), and its first error is the run's refusal.
        if (Turns.Count > 0 || answers is not (AcpRequestId.Init or AcpRequestId.New or AcpRequestId.Load or AcpRequestId.Config))
            return;

        pending.Clear();
        ToolCallStatus.Clear();
        ActiveAfterLastTurn = false;
        pricedSinceAnswer = false;
        if (error is not null)
        {
            if (HandshakeError is null)
            {
                HandshakeError = error;
                HandshakeTag = answers;
            }

            return;
        }

        if (answers == AcpRequestId.New)
        {
            Opened = true;
            if (AcpJson.StringOf(frame.Result ?? default, "sessionId") is { } newSession)
                SessionId = newSession;
        }
        else if (answers == AcpRequestId.Load)
        {
            (Opened, Loaded) = (true, true);
            SessionId = resumeSession ?? SessionId;
        }
    }

    private void OnUpdate(JsonElement parameters)
    {
        // opencode streams its child sessions' updates too (`_meta "opencode/child-session-updates"`): only the run's own session is read.
        if (AcpJson.StringOf(parameters, "sessionId") is { } updateSession)
        {
            SessionId ??= updateSession;
            if (updateSession != SessionId)
                return;
        }

        if (AcpJson.ObjectOf(parameters, "update") is not { } update)
            return;

        // A turn's own updates, whatever they carry; a session-level one (available_commands_update, a title) can come at any time.
        string? kind = AcpJson.StringOf(update, "sessionUpdate");
        if (kind is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update" or "usage_update" or "plan")
        {
            // 2026-10-16: a resumed run killed during the replay carried the previous session's turn as its receipt (confirm-pass
            // finding), so nothing of the agent's counts before the session is open, as in AcpStreamFormatter.
            if (!Opened)
                return;

            ActiveAfterLastTurn = true;
        }

        switch (kind)
        {
            case "agent_message_chunk":
                JsonElement content = AcpJson.ObjectOf(update, "content") ?? default;
                if (AcpJson.StringOf(content, "type") == "text" && AcpJson.StringOf(content, "text") is { } text)
                {
                    // A turn can say several messages (one before a tool call, one after): glued together they read "it.Created".
                    string? messageId = AcpJson.StringOf(update, "messageId");
                    if (pending.Length > 0 && messageId != pendingMessageId)
                        pending.Append("\n\n");

                    pendingMessageId = messageId;
                    pending.Append(text);
                }

                break;
            case "usage_update":
                JsonElement cost = AcpJson.ObjectOf(update, "cost") ?? default;
                if (AcpJson.DecimalOf(cost, "amount") is { } amount && string.Equals(AcpJson.StringOf(cost, "currency"), "USD", StringComparison.OrdinalIgnoreCase))
                    (RunningCostUsd, pricedSinceAnswer) = (amount, true);

                break;
            case "tool_call" or "tool_call_update":
                // A status is optional, and ACP's default is `pending`: a call never given one is still a call.
                if (AcpJson.StringOf(update, "toolCallId") is { } toolCallId)
                {
                    if (AcpJson.StringOf(update, "status") is { } status)
                        ToolCallStatus[toolCallId] = status;
                    else
                        ToolCallStatus.TryAdd(toolCallId, "pending");
                }

                break;
            default:
                // user_message_chunk (a load's replay), agent_thought_chunk, available_commands_update, plan: nothing more to keep.
                break;
        }
    }

    private static AcpError ErrorOf(JsonElement error)
    {
        JsonElement data = AcpJson.ObjectOf(error, "data") ?? default;
        return new AcpError(
            AcpJson.IntOf(error, "code") ?? 0,
            AcpJson.StringOf(error, "message") ?? "the agent answered with an error",
            AcpJson.StringOf(data, "errorName"),
            AcpJson.StringOf(data, "modelId"));
    }
}

// One answered prompt: its text is the turn's `agent_message_chunk`s, its usage this turn's own (not a running total, measured).
internal sealed record AcpTurn(string Text, string? StopReason, JsonElement? Usage, AcpError? Error, JsonElement? Raw)
{
    // A turn cut short of its work; `cancelled` is not one (a refused permission ends the turn there), and the session can go on.
    public bool Stopped => StopReason is "refusal" or "max_tokens" or "max_turn_requests";

    public bool Failed => Error is not null || Stopped;

    public string? Failure => Error?.Message ?? (Stopped ? $"the agent stopped: {StopReason}" : null);
}

// A JSON-RPC error: ErrorName is opencode's `data.errorName` (`provider.invalid-request`), ModelId the `data.modelId` of an unknown model.
internal sealed record AcpError(int Code, string Message, string? ErrorName, string? ModelId);
