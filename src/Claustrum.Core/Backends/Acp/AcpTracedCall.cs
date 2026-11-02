using System.Text.Json;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends.Acp;

// #145: one ACP tool call, gathered from its updates (pending, in_progress, then completed or failed) until AcpTrace hands it on. Every
// field keeps the last value an update gave; the line is the update that ended the call, or the last one seen while it was still open.
internal sealed class AcpTracedCall
{
    private string? name;
    private string? kind;
    private string? title;
    private JsonElement? rawInput;
    private string? location;
    private string? status;
    private string? error;
    private string? output;
    private int? exitCode;
    private int line;
    private bool ended;

    public void Take(JsonElement update, int number)
    {
        name ??= AcpJson.StringOf(update, "name");
        kind = AcpJson.StringOf(update, "kind") ?? kind;
        title = AcpJson.StringOf(update, "title") ?? title;
        if (AcpJson.ObjectOf(update, "rawInput") is { } input && input.EnumerateObject().Any())
            rawInput = input;
        if (LocationOf(update) is { } path)
            location = path;

        status = AcpJson.StringOf(update, "status") ?? status;
        if (status is "failed")
            error = AcpJson.StringOf(AcpJson.ObjectOf(update, "rawOutput") ?? default, "error") ?? TextOf(update) ?? error;
        else if (status is "completed")
            output = TextOf(update) ?? output;

        // An `execute` that ran reports its exit status in `rawOutput.metadata.exit` (measured 2026-10-27, job 0596d5af).
        if (AcpJson.ObjectOf(AcpJson.ObjectOf(update, "rawOutput") ?? default, "metadata") is { } metadata && AcpJson.IntOf(metadata, "exit") is { } exit)
            exitCode = exit;

        if (!ended)
            line = number;
        ended |= status is "completed" or "failed";
    }

    public TracedCall Traced()
    {
        // The kind says what the tool does; a missing tool's call is `other`, and only its name or title says which it was.
        ToolFamily family = kind switch
        {
            "execute" => ToolFamily.Shell,
            "read" => ToolFamily.Read,
            "search" => ToolFamily.Search,
            "edit" or "delete" or "move" => ToolFamily.Edit,
            "fetch" => ToolFamily.Fetch,
            _ => ToolNames.FamilyOf(name ?? title),
        };

        JsonElement input = rawInput ?? default;
        string? path = family == ToolFamily.Shell
            ? AcpJson.StringOf(input, "workdir") ?? AcpJson.StringOf(input, "cwd")
            : AcpJson.StringOf(input, "path") ?? AcpJson.StringOf(input, "filePath") ?? location;
        bool failed = status == "failed";
        return new TracedCall(
            name ?? title ?? kind ?? "",
            family,
            AcpJson.StringOf(input, "command"),
            path,
            AcpJson.StringOf(input, "url"),
            failed,
            failed ? error : null,
            family == ToolFamily.Shell ? output : null,
            line,
            exitCode);
    }

    private static string? LocationOf(JsonElement update) =>
        update.TryGetProperty("locations", out JsonElement locations) && locations.ValueKind == JsonValueKind.Array
            ? locations.EnumerateArray().Select(entry => AcpJson.StringOf(entry, "path")).FirstOrDefault(path => path is not null)
            : null;

    // `content: [{type: "content", content: {type: "text", text}}]`, the text items joined.
    private static string? TextOf(JsonElement update)
    {
        if (!update.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            return null;

        string[] texts = [.. content.EnumerateArray()
            .Select(item => AcpJson.ObjectOf(item, "content") is { } inner && AcpJson.StringOf(inner, "type") == "text" ? AcpJson.StringOf(inner, "text") : null)
            .OfType<string>()];
        return texts.Length == 0 ? null : string.Join('\n', texts);
    }
}
