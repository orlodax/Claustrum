using System.Text;
using System.Text.Json;
using Claustrum.Core.Live;

namespace Claustrum.Core.Backends.Claude;

// `--input-format stream-json` (measured 2026-10-15, NOTES.md "Live sessions"): one JSON user message per
// stdin line, one `result` event per turn on stdout, and the process outlives each turn until stdin closes.
public sealed class ClaudeLiveProtocol : ILiveProtocol
{
    public static readonly ClaudeLiveProtocol Instance = new();

    public string EncodeUserMessage(string text)
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
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
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
