using System.Globalization;
using System.Text.Json;

namespace Claustrum.Core.Backends.Claude;

// Claude Code's `--output-format stream-json` writes JSONL with `type:"assistant"` wrapping
// message.content blocks (text, tool_use), `type:"result"` with the final cost, and system events
// (init, post_turn_summary, rate_limit_event). Assistant blocks with tool_use or text content produce
// one line each, a failed tool result (`user` / is_error) a `✗` line; everything else prints nothing.
public sealed class ClaudeStreamFormatter(string cwd) : IStreamFormatter
{
    private decimal runningCost;

    public string? FormatLine(string rawLine)
    {
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

        // Anything that is not an object with a string `type` is not an event we know the shape of:
        // raw is the honest output, and a throwing property access here would kill the pump thread.
        if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type)
            return rawLine;

        long? timestampMs = TryGetTimestamp(root);

        // result: track final cost, print nothing.
        if (type == "result")
        {
            if (root.TryGetProperty("total_cost_usd", out JsonElement costProp)
                && costProp.ValueKind == JsonValueKind.Number
                && costProp.TryGetDecimal(out decimal total))
                runningCost = total;

            return null;
        }

        // assistant: look inside message.content for text or tool_use blocks.
        if (type == "assistant")
            return root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object
                ? FormatAssistant(message, timestampMs)
                : rawLine;

        // user: the tool results. Only a failed one is worth a line; `--stream raw` has the rest.
        if (type == "user")
            return root.TryGetProperty("message", out JsonElement userMessage) && userMessage.ValueKind == JsonValueKind.Object
                ? FormatUser(userMessage, timestampMs)
                : null;

        // system, rate_limit_event and every type this formatter does not know: print nothing.
        return null;
    }

    private string? FormatUser(JsonElement message, long? timestampMs)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return null;

        List<string> errors = [];
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || GetString(block, "type") != "tool_result"
                || !block.TryGetProperty("is_error", out JsonElement isError)
                || isError.ValueKind != JsonValueKind.True)
                continue;

            errors.Add(FormatLine(timestampMs, "\u2717", FoldText(ToolResultText(block))));
        }

        return errors.Count == 0 ? null : string.Join('\n', errors);
    }

    // A tool_result's `content` is a plain string, or an array of `{type:"text",text}` blocks.
    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
            return "tool failed";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind != JsonValueKind.Array)
            return "tool failed";

        return string.Join(' ', content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && GetString(part, "type") == "text")
            .Select(part => GetString(part, "text") ?? ""));
    }

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private string? FormatAssistant(JsonElement message, long? timestampMs)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return null;

        string? result = null;

        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = GetString(block, "type");

            if (blockType == "text")
            {
                string text = GetString(block, "text") ?? "";
                if (text.Length > 0)
                {
                    string folded = FoldText(text);
                    result = FormatLine(timestampMs, "SAYS", folded);
                }
            }
            else if (blockType == "tool_use")
            {
                if (GetString(block, "name") is not { } tool)
                    continue;

                // Extract the relevant input field for this tool.
                string detail = "";
                if (block.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object)
                {
                    if (GetString(input, "file_path") is { } fp)
                        detail = ShortenPath(fp);
                    else if (GetString(input, "command") is { } cmd)
                        detail = cmd;
                    else if (GetString(input, "pattern") is { } pat)
                        detail = pat;
                }

                string label = ToolLabel(tool);
                result = FormatLine(timestampMs, label, detail);
            }
        }

        return result;
    }

    private static long? TryGetTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out JsonElement tsProp) && tsProp.ValueKind == JsonValueKind.String)
        {
            string ts = tsProp.GetString() ?? "";
            if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime parsed))
                return new DateTimeOffset(parsed).ToUnixTimeMilliseconds();
        }

        return null;
    }

    private static string ToolLabel(string tool) => tool.Length <= 5
        ? tool.ToUpperInvariant()
        : tool[..5].ToUpperInvariant();

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

            int trimmed = written;
            while (trimmed > 0 && buffer[trimmed - 1] == ' ')
                trimmed--;

            string result = buffer[..Math.Min(trimmed, 140)].ToString();
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

    private string ShortenPath(string path)
    {
        if (path.Length == 0)
            return path;

        if (cwd.Length > 0 && path.StartsWith(cwd, StringComparison.Ordinal))
        {
            string relative = path[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Length > 0 ? relative : path;
        }

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
}
