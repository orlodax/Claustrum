using System.Text;
using System.Text.Json;
using Claustrum.Core.Live;

namespace Claustrum.Core.Backends.Claude;

// `--input-format stream-json` (measured 2026-10-15, NOTES.md "Live sessions"): one JSON user message per
// stdin line, one `result` event per turn on stdout, and the process outlives each turn until stdin closes.
// `--replay-user-messages` (#127) adds an echo of each message when claude takes it: `type:"user"`, `isReplay:true`, the message's `uuid`.
public sealed class ClaudeLiveProtocol : IStreamLiveProtocol
{
    public static readonly ClaudeLiveProtocol Instance = new();

    /// <summary>A line claude marks as replayed: an echo of a message it took, or a message of the resumed session's history. Not an event of this run.</summary>
    internal static bool IsReplay(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("isReplay", out JsonElement replay) && replay.ValueKind == JsonValueKind.True;

    public string EncodeUserMessage(string text, string? uuid = null)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            if (uuid is not null)
                writer.WriteString("uuid", uuid);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public LiveReplay? ReadReplay(string stdoutLine)
    {
        // Most lines of a stream are not echoes: the marker is the cheap test, before any JSON is parsed.
        if (!stdoutLine.Contains("\"isReplay\"", StringComparison.Ordinal))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(stdoutLine);
            JsonElement root = document.RootElement;
            return IsReplay(root) && ClaudeBackend.TryGetString(root, "type") == "user" ? new LiveReplay(ClaudeBackend.TryGetString(root, "uuid")) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public LiveTurn? ReadTurnEnd(string stdoutLine)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdoutLine);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ClaudeBackend.TryGetString(root, "type") != "result")
                return null;

            bool isError = root.TryGetProperty("is_error", out JsonElement errorProp) && errorProp.ValueKind == JsonValueKind.True;
            bool failedSubtype = ClaudeBackend.TryGetString(root, "subtype") is { } subtype && subtype.StartsWith("error_", StringComparison.Ordinal);
            return new LiveTurn(ClaudeBackend.FinalMessage(root), isError || failedSubtype);
        }
    }
}
