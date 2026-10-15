using System.Globalization;
using System.Text.Json;

namespace Claustrum.Core.Backends.Claude;

// Claude Code's `--output-format stream-json` writes JSONL with `type:"assistant"` wrapping
// message.content blocks (text, tool_use), `type:"result"` with the final cost, and system events
// (init, post_turn_summary, rate_limit_event). Assistant blocks with tool_use or text content produce
// one line each, a failed tool result (`user` / is_error) a `✗` line, `result` a `total $` line;
// everything else prints nothing. The stream has no timestamps: see StreamText.Time.
public sealed class ClaudeStreamFormatter(string cwd) : IStreamFormatter
{
    public string? FormatLine(string rawLine) => FormatLine(rawLine, replayed: false);

    public string? FormatLine(string rawLine, bool replayed)
    {
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

        // Anything that is not an object with a string `type` is not an event we know the shape of:
        // raw is the honest output, and a throwing property access here would kill the pump thread.
        if (root.ValueKind != JsonValueKind.Object || GetString(root, "type") is not { } type)
            return Passthrough(rawLine);

        long? timestampMs = TryGetTimestamp(root);

        // result: the one place the total is known, so the one line that shows it.
        if (type == "result")
            return root.TryGetProperty("total_cost_usd", out JsonElement costProp)
                && costProp.ValueKind == JsonValueKind.Number
                && costProp.TryGetDecimal(out decimal total)
                    ? FormatLine(timestampMs, replayed, "DONE", $"total ${total.ToString("0.0000##", CultureInfo.InvariantCulture)}")
                    : null;

        // assistant: look inside message.content for text or tool_use blocks.
        if (type == "assistant")
            return root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object
                ? FormatAssistant(message, timestampMs, replayed)
                : Passthrough(rawLine);

        // user: the tool results. Only a failed one is worth a line; `--stream-raw` has the rest.
        if (type == "user")
            return root.TryGetProperty("message", out JsonElement userMessage) && userMessage.ValueKind == JsonValueKind.Object
                ? FormatUser(userMessage, timestampMs, replayed)
                : null;

        // system, rate_limit_event and every type this formatter does not know: print nothing.
        return null;
    }

    // Not an event we know the shape of: still untrusted text, so it goes out sanitized, never as-is.
    private static string? Passthrough(string rawLine) => StreamText.Sanitize(rawLine) is { Length: > 0 } line ? line : null;

    private static string? FormatUser(JsonElement message, long? timestampMs, bool replayed)
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

            errors.Add(FormatLine(timestampMs, replayed, "\u2717", StreamText.Sanitize(ToolResultText(block), StreamText.TextLimit)));
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

    private string? FormatAssistant(JsonElement message, long? timestampMs, bool replayed)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return null;

        string? result = null;

        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = GetString(block, "type");

            if (blockType == "text")
            {
                string text = StreamText.Sanitize(GetString(block, "text"), StreamText.TextLimit);
                if (text.Length > 0)
                    result = FormatLine(timestampMs, replayed, "SAYS", text);
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
                        detail = StreamText.Sanitize(ShortenPath(fp));
                    else if (GetString(input, "command") is { } cmd)
                        detail = StreamText.Sanitize(cmd);
                    else if (GetString(input, "pattern") is { } pat)
                        detail = StreamText.Sanitize(pat);
                }

                result = FormatLine(timestampMs, replayed, ToolLabel(tool), detail);
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

    private static string ToolLabel(string tool)
    {
        string label = StreamText.Sanitize(tool);
        return (label.Length <= 5 ? label : label[..5]).ToUpperInvariant();
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

    private static string FormatLine(long? timestampMs, bool replayed, string label, string detail) =>
        $"{StreamText.Time(timestampMs, replayed)} {label,-5} {detail}";
}
