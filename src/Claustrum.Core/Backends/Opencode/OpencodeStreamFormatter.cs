using System.Text.Json;

namespace Claustrum.Core.Backends.Opencode;

// docs/NOTES.md "The opencode and api backends, validated against real endpoints" — the same event
// shapes OpencodeBackend.Parse reads, in streaming form: step_start, tool_use, text, step_finish,
// error (NOTES.md). step_boundaries print nothing; tool_use and text produce one line each.
public sealed class OpencodeStreamFormatter(string cwd) : IStreamFormatter
{
    private decimal runningCost;

    public string? FormatLine(string rawLine) => FormatLine(rawLine, replayed: false);

    public string? FormatLine(string rawLine, bool replayed)
    {
        // ParseOnce, then decide — no throw path for the common no-parse events.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawLine);
        }
        catch (JsonException)
        {
            return Passthrough(rawLine);
        }

        using JsonDocument _ = document;
        JsonElement root = document.RootElement;

        // Not an object with a string `type`: not an event we know the shape of, so the sanitized
        // line goes out and no property access below can throw on the pump thread.
        if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type)
            return Passthrough(rawLine);

        long? timestampMs = root.TryGetProperty("timestamp", out JsonElement tsProp)
            && tsProp.ValueKind == JsonValueKind.Number
            && tsProp.TryGetInt64(out long ts)
                ? ts
                : null;

        // error: one human line with the error message.
        if (type == "error")
        {
            string message = ExtractErrorMessage(root, rawLine);
            return FormatLine(timestampMs, replayed, "ERROR", StreamText.Sanitize(message));
        }

        // step_finish: track cost, print nothing (with or without a numeric cost).
        if (type == "step_finish")
        {
            if (root.TryGetProperty("part", out JsonElement finishPart)
                && finishPart.ValueKind == JsonValueKind.Object
                && finishPart.TryGetProperty("cost", out JsonElement costProp)
                && costProp.ValueKind == JsonValueKind.Number
                && costProp.TryGetDecimal(out decimal stepCost))
                runningCost += stepCost;

            return null;
        }

        // text: fold whitespace, cut at ~140 chars; a part with no text prints nothing.
        if (type == "text")
            return root.TryGetProperty("part", out JsonElement textPart)
                && StreamText.Sanitize(GetString(textPart, "text"), StreamText.TextLimit) is { Length: > 0 } text
                    ? FormatLine(timestampMs, replayed, "SAYS", text)
                    : null;

        // tool_use: the interesting one.
        if (type == "tool_use" && root.TryGetProperty("part", out JsonElement toolPart) && toolPart.ValueKind == JsonValueKind.Object)
            return FormatToolUse(toolPart, timestampMs, replayed);

        // step_start, heartbeat and every type this formatter does not know: print nothing.
        return null;
    }

    // Not an event we know the shape of: still untrusted text, so it goes out sanitized, never as-is.
    private static string? Passthrough(string rawLine) => StreamText.Sanitize(rawLine) is { Length: > 0 } line ? line : null;

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private string? FormatToolUse(JsonElement part, long? timestampMs, bool replayed)
    {
        if (GetString(part, "tool") is not { } tool)
            return null;

        // Check for error state.
        if (part.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.Object)
        {
            string? status = GetString(state, "status");

            if (status is "completed" or "pending")
            {
                string? inputText = null;

                if (state.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object)
                {
                    // File operations: try filePath then path.
                    if (GetString(input, "filePath") is { } fp)
                        inputText = ShortenPath(fp);
                    else if (GetString(input, "path") is { } p)
                        inputText = ShortenPath(p);
                    else if (GetString(input, "command") is { } cmd)
                        inputText = cmd;
                    else if (GetString(input, "pattern") is { } pat)
                        inputText = pat;
                }

                return FormatLine(timestampMs, replayed, ToolLabel(tool), StreamText.Sanitize(inputText));
            }

            // Failed tool call: show ✗ error.
            string message = state.TryGetProperty("error", out JsonElement errorProp)
                ? (errorProp.ValueKind == JsonValueKind.String ? errorProp.GetString() ?? "" : "")
                : $"{tool} failed";
            return FormatLine(timestampMs, replayed, "\u2717", StreamText.Sanitize(message, StreamText.TextLimit));
        }

        return null;
    }

    private static string ToolLabel(string tool)
    {
        string label = StreamText.Sanitize(tool);
        return (label.Length <= 5 ? label : label[..5]).ToUpperInvariant();
    }

    // Show path relative to cwd when possible; always keep it short.
    private string ShortenPath(string path)
    {
        if (path.Length == 0)
            return path;

        if (cwd.Length > 0 && path.StartsWith(cwd, StringComparison.Ordinal))
        {
            string relative = path[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Length > 0 ? relative : path;
        }

        // Keep the last two segments when the path is long.
        if (path.Length > 60)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            string file = Path.GetFileName(path);
            if (dir.Length > 40)
                return $"…{dir[^30..]}{Path.DirectorySeparatorChar}{file}";
        }

        return path;
    }

    private string FormatLine(long? timestampMs, bool replayed, string label, string detail)
    {
        string cost = runningCost > 0
            ? $" (${runningCost:F6})"
            : "";

        return $"{StreamText.Time(timestampMs, replayed)} {label,-5} {detail}{cost}";
    }

    // Two genuinely captured error shapes (see OpencodeBackend.ExtractErrorMessage).
    private static string ExtractErrorMessage(JsonElement root, string rawLine)
    {
        if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
            return rawLine;

        if (error.TryGetProperty("message", out JsonElement flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString() ?? rawLine;

        return error.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && GetString(data, "message") is { } nested
                ? nested
                : rawLine;
    }
}
