using System.Text;
using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// `--stream` and `jobs follow` for an ACP run, laid out like OpencodeStreamFormatter. ACP streams the reply in chunks, so this one keeps
// state: a message's chunks are one SAYS line, printed when the message changes, a tool line or a prompt's answer comes, or the text
// passes StreamText.TextLimit (the rest of that message is then not printed). A tool call is one line, the first time it runs with a
// title; a failed one adds a ✗ line. Frames carry no time: StreamText.Time(null, replayed).
public sealed class AcpStreamFormatter(string cwd) : IStreamFormatter
{
    private readonly StringBuilder says = new();
    private readonly Dictionary<string, ToolCall> tools = new(StringComparer.Ordinal);
    private string? saysMessage;
    private bool saysCut;
    private string? sessionId;

    // Updates before the session is open are a loaded session's history, replayed ahead of the `session/load` answer: its
    // chunks and its tool calls both (measured 2026-10-16).
    private bool opened;
    private decimal runningCost;

    public string? FormatLine(string rawLine) => FormatLine(rawLine, replayed: false);

    public string? FormatLine(string rawLine, bool replayed)
    {
        if (AcpFrame.TryParse(rawLine) is not { } frame)
            return Passthrough(rawLine);

        if (frame.IsResponse)
            return Response(frame, replayed);

        return frame is { IsNotification: true, Method: "session/update", Params: { } parameters } ? Update(parameters, replayed) : null;
    }

    // Not a frame: still untrusted text, so it goes out sanitized, never as-is.
    private static string? Passthrough(string rawLine) => StreamText.Sanitize(rawLine) is { Length: > 0 } line ? line : null;

    private string? Response(AcpFrame frame, bool replayed)
    {
        string? answers = frame.Answers;
        if (frame.Error is { } error)
            return Join(FlushSays(replayed), Line(replayed, "ERROR", StreamText.Sanitize(AcpJson.StringOf(error, "message") ?? "the agent answered with an error")));

        JsonElement result = frame.Result ?? default;
        if (answers == AcpRequestId.Prompt)
        {
            string? flushed = FlushSays(replayed);
            return AcpJson.StringOf(result, "stopReason") is { } stop and not "end_turn"
                ? Join(flushed, Line(replayed, "STOP", StreamText.Sanitize(stop)))
                : flushed;
        }

        if (answers is AcpRequestId.New or AcpRequestId.Load or AcpRequestId.Config)
        {
            opened = true;
            says.Clear();
            if (answers == AcpRequestId.New && AcpJson.StringOf(result, "sessionId") is { } newSession)
                sessionId = newSession;
        }

        return null;
    }

    private string? Update(JsonElement parameters, bool replayed)
    {
        // Updates of opencode's child sessions are not this run's.
        if (AcpJson.StringOf(parameters, "sessionId") is { } updateSession)
        {
            sessionId ??= updateSession;
            if (updateSession != sessionId)
                return null;
        }

        if (!opened || AcpJson.ObjectOf(parameters, "update") is not { } update)
            return null;

        switch (AcpJson.StringOf(update, "sessionUpdate"))
        {
            case "agent_message_chunk":
                return Chunk(update, replayed);
            case "tool_call" or "tool_call_update":
                return Tool(update, replayed);
            case "usage_update":
                // A running total: assigned, never added.
                if (AcpJson.ObjectOf(update, "cost") is { } cost && AcpJson.DecimalOf(cost, "amount") is { } amount
                    && string.Equals(AcpJson.StringOf(cost, "currency"), "USD", StringComparison.OrdinalIgnoreCase))
                    runningCost = amount;

                return null;
            default:
                return null;
        }
    }

    private string? Chunk(JsonElement update, bool replayed)
    {
        string? messageId = AcpJson.StringOf(update, "messageId");
        string? flushed = null;
        if (messageId != saysMessage)
        {
            flushed = FlushSays(replayed);
            saysMessage = messageId;
            saysCut = false;
        }

        JsonElement content = AcpJson.ObjectOf(update, "content") ?? default;
        if (saysCut || AcpJson.StringOf(content, "type") != "text" || AcpJson.StringOf(content, "text") is not { } text)
            return flushed;

        says.Append(text);
        if (StreamText.Sanitize(says.ToString(), StreamText.TextLimit).Length <= StreamText.TextLimit)
            return flushed;

        saysCut = true;
        return Join(flushed, FlushSays(replayed));
    }

    private string? Tool(JsonElement update, bool replayed)
    {
        if (AcpJson.StringOf(update, "toolCallId") is not { } toolCallId)
            return null;

        if (!tools.TryGetValue(toolCallId, out ToolCall? tool))
        {
            tool = new ToolCall();
            tools[toolCallId] = tool;
        }

        tool.Read(update, cwd);
        string? status = AcpJson.StringOf(update, "status");
        if (status is "in_progress" or "completed" && !tool.Shown && tool.Title is not null)
        {
            tool.Shown = true;
            return Join(FlushSays(replayed), Line(replayed, tool.Label, StreamText.Sanitize(tool.Detail, StreamText.TextLimit)));
        }

        if (status != "failed" || tool.Failed)
            return null;

        tool.Failed = true;
        string failure = tool.Error is { } why ? $"{tool.Title ?? tool.Label}: {why}" : tool.Title ?? tool.Label;
        return Join(FlushSays(replayed), Line(replayed, "✗", StreamText.Sanitize(failure, StreamText.TextLimit)));
    }

    private string? FlushSays(bool replayed)
    {
        string text = StreamText.Sanitize(says.ToString(), StreamText.TextLimit);
        says.Clear();
        return text.Length > 0 ? Line(replayed, "SAYS", text) : null;
    }

    private string Line(bool replayed, string label, string detail)
    {
        string cost = runningCost > 0 ? $" (${runningCost:F6})" : "";
        return $"{StreamText.Time(null, replayed)} {label,-5} {detail}{cost}";
    }

    // One call, one string: a flushed message and the line that flushed it go out together.
    private static string? Join(string? first, string? second) => first is null ? second : second is null ? first : $"{first}\n{second}";

    // What one tool call has said so far: `tool_call` arrives pending with a bare title, `tool_call_update` later with the command.
    private sealed class ToolCall
    {
        private string? name;
        private string? kind;
        private string? command;
        private string? path;
        private string? pattern;

        public string? Title { get; private set; }

        // opencode's tool `name` (shell, write, glob) comes with the first `tool_call` only; else ACP's `kind`. Five letters, as opencode's.
        public string Label
        {
            get
            {
                string label = StreamText.Sanitize(name ?? kind ?? "tool");
                return (label.Length <= 5 ? label : label[..5]).ToUpperInvariant();
            }
        }

        public string Detail => command ?? path ?? pattern ?? Title ?? "";

        public string? Error { get; private set; }

        public bool Shown { get; set; }

        public bool Failed { get; set; }

        public void Read(JsonElement update, string cwd)
        {
            Title = AcpJson.StringOf(update, "title") ?? Title;
            name = AcpJson.StringOf(update, "name") ?? name;
            kind = AcpJson.StringOf(update, "kind") ?? kind;

            JsonElement input = AcpJson.ObjectOf(update, "rawInput") ?? default;
            command = AcpJson.StringOf(input, "command") ?? command;
            path = (AcpJson.StringOf(input, "filePath") ?? AcpJson.StringOf(input, "path")) is { } file ? Shorten(file, cwd) : path;
            pattern = AcpJson.StringOf(input, "pattern") ?? pattern;
            Error = AcpJson.StringOf(AcpJson.ObjectOf(update, "rawOutput") ?? default, "error") ?? Error;
        }

        private static string Shorten(string file, string cwd) =>
            cwd.Length > 0 && file.StartsWith(cwd, StringComparison.Ordinal) && file[cwd.Length..].TrimStart('/', '\\') is { Length: > 0 } relative
                ? relative
                : file;
    }
}
