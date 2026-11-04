using System.Text.Json;

namespace Claustrum.Core.Backends.Codex;

// `codex exec --json` events, as CodexBackend.Parse reads them (tests/fixtures/codex): one line per finished item, a `Reconnecting…`
// notice as RETRY, any other error event as ERROR, and turn.failed only when it says something new (it repeats the last error
// event in every fixture). `item.started` and the bookkeeping events print nothing. No timestamps in the stream: stamped on arrival.
public sealed class CodexStreamFormatter : IStreamFormatter
{
    private const string RetryStart = "Reconnecting...";

    private string? lastError;

    public string? FormatLine(string rawLine) => FormatLine(rawLine, replayed: false);

    public string? FormatLine(string rawLine, bool replayed)
    {
        if (CodexBackend.Event(rawLine) is not { } root)
            return StreamText.Sanitize(rawLine) is { Length: > 0 } line ? line : null;

        return CodexBackend.StringOf(root, "type") switch
        {
            "item.completed" when CodexBackend.ObjectOf(root, "item") is { } item => FormatItem(item, replayed),
            "error" => ErrorLine(replayed, CodexBackend.StringOf(root, "message")),
            "turn.failed" => FailedLine(replayed, CodexBackend.ObjectOf(root, "error") is { } error ? CodexBackend.StringOf(error, "message") : null),
            _ => null,
        };
    }

    private string? ErrorLine(bool replayed, string? message)
    {
        if (message?.StartsWith(RetryStart, StringComparison.Ordinal) == true)
            return Line(replayed, "RETRY", message);

        lastError = message;
        return Line(replayed, "ERROR", message);
    }

    private string? FailedLine(bool replayed, string? message) => message == lastError ? null : Line(replayed, "ERROR", message);

    private static string? FormatItem(JsonElement item, bool replayed) => CodexBackend.StringOf(item, "type") switch
    {
        "agent_message" => Line(replayed, "SAYS", CodexBackend.StringOf(item, "text")),
        "command_execution" when CodexBackend.StringOf(item, "status") == "failed" =>
            Line(replayed, "✗", CodexBackend.StringOf(item, "command")),
        "command_execution" => Line(replayed, "SHELL", CodexBackend.StringOf(item, "command")),
        "file_change" => Line(replayed, "EDIT", ChangedPaths(item)),
        "error" => Line(replayed, "WARN", CodexBackend.StringOf(item, "message")),
        _ => null,
    };

    private static string? ChangedPaths(JsonElement item) =>
        item.TryGetProperty("changes", out JsonElement changes) && changes.ValueKind == JsonValueKind.Array
            ? string.Join(", ", changes.EnumerateArray().Where(change => change.ValueKind == JsonValueKind.Object).Select(change => CodexBackend.StringOf(change, "path")).OfType<string>())
            : null;

    private static string? Line(bool replayed, string label, string? detail) =>
        StreamText.Sanitize(detail, StreamText.TextLimit) is { Length: > 0 } text ? $"{StreamText.Time(null, replayed)} {label,-5} {text}" : null;
}
