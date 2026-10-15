using System.Globalization;
using System.Text.Json;

namespace Claustrum.Core.Backends.Claude;

// Claude Code's `--output-format stream-json` writes JSONL with `type:"assistant"` wrapping
// message.content blocks (text, tool_use), `type:"result"` with the final cost, and system events
// (init, post_turn_summary, rate_limit_event). Only assistant blocks with tool_use or text content
// produce one line each; everything else prints nothing.
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

        string? type = root.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;
        long? timestampMs = TryGetTimestamp(root);

        // result: track final cost, print nothing.
        if (type == "result")
        {
            if (root.TryGetProperty("total_cost_usd", out JsonElement costProp)
                && costProp.TryGetDecimal(out decimal total))
                runningCost = total;

            return null;
        }

        // system and rate_limit events: print nothing.
        if (type is "system" or "rate_limit_event")
            return null;

        // assistant: look inside message.content for text or tool_use blocks.
        if (type == "assistant" && root.TryGetProperty("message", out JsonElement message))
            return FormatAssistant(message, timestampMs);

        return rawLine;
    }

    private string? FormatAssistant(JsonElement message, long? timestampMs)
    {
        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return null;

        string? result = null;

        foreach (JsonElement block in content.EnumerateArray())
        {
            string? blockType = block.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;

            if (blockType == "text")
            {
                string text = block.TryGetProperty("text", out JsonElement textProp) ? textProp.GetString() ?? "" : "";
                if (text.Length > 0)
                {
                    string folded = FoldText(text);
                    result = FormatLine(timestampMs, "SAYS", folded);
                }
            }
            else if (blockType == "tool_use")
            {
                string? tool = block.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() : null;
                if (tool is null)
                    continue;

                // Extract the relevant input field for this tool.
                string detail = "";
                if (block.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object)
                {
                    if (input.TryGetProperty("file_path", out JsonElement fp))
                        detail = ShortenPath(fp.GetString() ?? "");
                    else if (input.TryGetProperty("command", out JsonElement cmd))
                        detail = cmd.GetString() ?? "";
                    else if (input.TryGetProperty("pattern", out JsonElement pat))
                        detail = pat.GetString() ?? "";
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
