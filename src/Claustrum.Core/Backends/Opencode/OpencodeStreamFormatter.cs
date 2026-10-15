using System.Globalization;
using System.Text.Json;

namespace Claustrum.Core.Backends.Opencode;

// docs/NOTES.md "The opencode and api backends, validated against real endpoints" — the same event
// shapes OpencodeBackend.Parse reads, in streaming form: step_start, tool_use, text, step_finish,
// error (NOTES.md). step_boundaries print nothing; tool_use and text produce one line each.
public sealed class OpencodeStreamFormatter(string cwd) : IStreamFormatter
{
    private decimal runningCost;

    public string? FormatLine(string rawLine)
    {
        // ParseOnce, then decide — no throw path for the common no-parse events.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawLine);
        }
        catch (JsonException)
        {
            return rawLine;
        }

        using JsonDocument _ = document;
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("type", out JsonElement typeProp))
            return rawLine;

        string? type = typeProp.GetString();
        long? timestampMs = root.TryGetProperty("timestamp", out JsonElement tsProp)
            && tsProp.TryGetInt64(out long ts)
                ? ts
                : null;

        // error: one human line with the error message.
        if (type == "error")
        {
            string message = ExtractErrorMessage(root, rawLine);
            return FormatLine(timestampMs, "ERROR", message);
        }

        // step_finish: track cost, print nothing.
        if (type == "step_finish" && root.TryGetProperty("part", out JsonElement finishPart)
            && finishPart.TryGetProperty("cost", out JsonElement costProp)
            && costProp.TryGetDecimal(out decimal stepCost))
        {
            runningCost += stepCost;
            return null;
        }

        // step_start, heartbeat, etc.: print nothing.
        if (type is "step_start" or "heartbeat")
            return null;

        // text: fold newlines, cut at ~140 chars.
        if (type == "text" && root.TryGetProperty("part", out JsonElement textPart)
            && textPart.TryGetProperty("text", out JsonElement textProp))
        {
            string text = textProp.GetString() ?? "";
            string folded = FoldText(text);
            return FormatLine(timestampMs, "SAYS", folded);
        }

        // tool_use: the interesting one.
        if (type == "tool_use" && root.TryGetProperty("part", out JsonElement toolPart))
            return FormatToolUse(toolPart, timestampMs);

        // Unknown event type: pass through.
        return rawLine;
    }

    private string? FormatToolUse(JsonElement part, long? timestampMs)
    {
        string? tool = part.TryGetProperty("tool", out JsonElement toolProp) ? toolProp.GetString() : null;
        if (tool is null)
            return null;

        // Check for error state.
        if (part.TryGetProperty("state", out JsonElement state))
        {
            string? status = state.TryGetProperty("status", out JsonElement statusProp)
                ? statusProp.GetString()
                : null;

            if (status is "completed" or "pending")
            {
                string? inputText = null;

                if (state.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object)
                {
                    // File operations: try filePath then path.
                    if (input.TryGetProperty("filePath", out JsonElement fp))
                        inputText = ShortenPath(fp.GetString() ?? "");
                    else if (input.TryGetProperty("path", out JsonElement p))
                        inputText = ShortenPath(p.GetString() ?? "");
                    else if (input.TryGetProperty("command", out JsonElement cmd))
                        inputText = cmd.GetString();
                    else if (input.TryGetProperty("pattern", out JsonElement pat))
                        inputText = pat.GetString();
                }

                string label = ToolLabel(tool);
                string detail = inputText ?? "";
                return FormatLine(timestampMs, label, detail);
            }

            // Failed tool call: show ✗ error.
            string message = state.TryGetProperty("error", out JsonElement errorProp)
                ? (errorProp.GetString() ?? "")
                : $"{tool} failed";
            return FormatLine(timestampMs, "\u2717", message);
        }

        return null;
    }

    private static string ToolLabel(string tool) => tool.Length <= 5
        ? tool.ToUpperInvariant()
        : tool[..5].ToUpperInvariant();

    // Fold consecutive whitespace to one space, trim, cut at ~140 chars.
    private static string FoldText(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length == 0)
            return "";

        int written = 0;
        bool inSpace = false;
        char[]? rented = null;
        int cap = Math.Min(span.Length, 145);
        Span<char> buffer = cap <= 256 ? stackalloc char[256] : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(cap));

        try
        {
            foreach (char ch in span)
            {
                if (ch is '\r' or '\n')
                {
                    if (!inSpace)
                    {
                        inSpace = true;
                        if (written < buffer.Length)
                            buffer[written++] = ' ';
                    }
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (!inSpace)
                    {
                        inSpace = true;
                        if (written < buffer.Length)
                            buffer[written++] = ' ';
                    }
                    continue;
                }

                inSpace = false;
                if (written < buffer.Length)
                    buffer[written++] = ch;

                if (written >= 140)
                    break;
            }

            // Trim trailing space.
            int trimmed = written;
            while (trimmed > 0 && buffer[trimmed - 1] == ' ')
                trimmed--;

            string result = buffer[..Math.Min(trimmed, 140)].ToString();

            // Add ellipsis if truncated.
            if (trimmed >= 140 && span.Length > 140)
                result += "…";

            return result;
        }
        finally
        {
            if (rented is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
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

    private string FormatLine(long? timestampMs, string label, string detail)
    {
        string time = timestampMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        string cost = runningCost > 0
            ? $" (${runningCost:F6})"
            : "";

        return $"{time} {label,-5} {detail}{cost}";
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
            && data.TryGetProperty("message", out JsonElement nested)
                ? nested.GetString() ?? rawLine
                : rawLine;
    }
}
