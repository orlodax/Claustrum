using System.Text.Json;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends.Acp;

// #145: an ACP run's tool use, for EnvironmentClassifier. AcpTranscript decides which updates are this run's (the session open, its own
// session id, nothing of a load's replay); each `toolCallId` gathers its updates: `name` only on `tool_call`, `kind` missing on `completed`,
// a `failed` one's `rawOutput.error`, a `completed` one's text content. Shapes: jobs 20261029-094020-089b47aa and -092218-3172ff82.
internal static class AcpTrace
{
    public static RunTrace Read(string stdout)
    {
        AcpTranscript transcript = new();
        Dictionary<string, AcpTracedCall> calls = [];
        List<string> order = [];
        string[] lines = stdout.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (transcript.Feed(lines[index].Trim()) is not { } frame)
                continue;

            // A handshake answer before any prompt forgets what came before it, as AcpTranscript forgets its tool calls.
            if (frame.IsResponse && transcript.Turns.Count == 0 && frame.Answers is AcpRequestId.Init or AcpRequestId.New or AcpRequestId.Load or AcpRequestId.Config)
            {
                calls.Clear();
                order.Clear();
                continue;
            }

            if (ToolUpdate(frame, transcript) is not { } update || AcpJson.StringOf(update, "toolCallId") is not { } id)
                continue;

            if (!calls.TryGetValue(id, out AcpTracedCall? call))
            {
                calls[id] = call = new AcpTracedCall();
                order.Add(id);
            }

            call.Take(update, index + 1);
        }

        return new RunTrace([.. order.Select(id => calls[id].Traced())], []);
    }

    private static JsonElement? ToolUpdate(AcpFrame frame, AcpTranscript transcript)
    {
        if (frame is not { IsNotification: true, Method: "session/update", Params: { } parameters } || !transcript.Opened)
            return null;

        // opencode's child sessions report under the run's own id (`_meta.opencode/child-session`): those are kept, as the run's work.
        if (AcpJson.StringOf(parameters, "sessionId") is { } session && transcript.SessionId is { } own && session != own)
            return null;

        return AcpJson.ObjectOf(parameters, "update") is { } update && AcpJson.StringOf(update, "sessionUpdate") is "tool_call" or "tool_call_update" ? update : null;
    }
}
