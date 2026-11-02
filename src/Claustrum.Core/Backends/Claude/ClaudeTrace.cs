using System.Text.Json;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends.Claude;

// #145: a claude stream-json run's tool use, for EnvironmentClassifier. The first `system/init` says the permission mode the session started
// in; `tool_use` items come in assistant messages and their `tool_result`s in user ones (replayed lines are another run's, skipped as in
// Parse). A refusal is said twice: a `system/permission_denied` line when it happens, and the turn's `result` `permission_denials`; both are
// read, once per call, so a run killed before its last `result` keeps them. Shapes: job 20261029-095220-0da9d76d (lines 3, 28, 112).
internal static class ClaudeTrace
{
    public static RunTrace Read(string stdout)
    {
        Dictionary<string, (string Name, JsonElement Input, int Line)> uses = [];
        Dictionary<string, (bool Failed, string Text, int Line)> results = [];
        List<string> order = [];
        List<(string Tool, string? Id, JsonElement Input, int Line)> denials = [];
        HashSet<string> denied = [];
        string? mode = null;
        int modeLine = 0;
        bool sawInit = false;

        string[] lines = stdout.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (Parse(lines[index]) is not { } root)
                continue;

            int number = index + 1;
            switch (ClaudeBackend.TryGetString(root, "type"))
            {
                case "system" when !sawInit && ClaudeBackend.TryGetString(root, "subtype") == "init":
                    sawInit = true;
                    (mode, modeLine) = (ClaudeBackend.TryGetString(root, "permissionMode"), number);
                    break;
                case "system" when ClaudeBackend.TryGetString(root, "subtype") == "permission_denied":
                    Deny(root, number, denials, denied);
                    break;
                case "assistant":
                    foreach (JsonElement item in Items(root, "tool_use"))
                    {
                        if (ClaudeBackend.TryGetString(item, "id") is not { } id || uses.ContainsKey(id))
                            continue;

                        uses[id] = (ClaudeBackend.TryGetString(item, "name") ?? "", Input(item, "input"), number);
                        order.Add(id);
                    }

                    break;
                case "user":
                    foreach (JsonElement item in Items(root, "tool_result"))
                    {
                        if (ClaudeBackend.TryGetString(item, "tool_use_id") is { } id)
                            results[id] = (item.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.True, ContentText(item), number);
                    }

                    break;
                case "result":
                    if (root.TryGetProperty("permission_denials", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement denial in list.EnumerateArray().Where(denial => denial.ValueKind == JsonValueKind.Object))
                            Deny(denial, number, denials, denied);
                    }

                    break;
                default:
                    break;
            }
        }

        TracedCall[] calls = [.. order.Select(id => Call(uses[id], results.TryGetValue(id, out (bool, string, int) result) ? result : null))];
        return new RunTrace(calls, [.. denials.Select(denial => Resolved(denial, uses, results))], mode, modeLine);
    }

    // A call is listed once, whichever line said it first: the system line, or a live session's result for each turn.
    private static void Deny(JsonElement source, int line, List<(string Tool, string? Id, JsonElement Input, int Line)> denials, HashSet<string> denied)
    {
        if (ClaudeBackend.TryGetString(source, "tool_name") is not { } tool)
            return;

        string? id = ClaudeBackend.TryGetString(source, "tool_use_id");
        if (id is null || denied.Add(id))
            denials.Add((tool, id, Input(source, "tool_input"), line));
    }

    // The line the refusal came back on (the refused call's tool_result) and what the call was given: the system line carries no input.
    private static TracedDenial Resolved(
        (string Tool, string? Id, JsonElement Input, int Line) denial,
        Dictionary<string, (string Name, JsonElement Input, int Line)> uses,
        Dictionary<string, (bool Failed, string Text, int Line)> results)
    {
        int line = denial.Id is not null && results.TryGetValue(denial.Id, out (bool, string, int Line) result) ? result.Line : denial.Line;
        JsonElement input = denial.Input.ValueKind == JsonValueKind.Object ? denial.Input
            : denial.Id is not null && uses.TryGetValue(denial.Id, out (string, JsonElement Input, int) use) ? use.Input
            : default;
        return new TracedDenial(denial.Tool, StringOf(input, "command"), PathOf(input), StringOf(input, "url"), line);
    }

    private static TracedCall Call((string Name, JsonElement Input, int Line) use, (bool Failed, string Text, int Line)? result)
    {
        ToolFamily family = ToolNames.FamilyOf(use.Name);
        bool failed = result?.Failed ?? false;
        return new TracedCall(
            use.Name,
            family,
            StringOf(use.Input, "command"),
            PathOf(use.Input),
            StringOf(use.Input, "url"),
            failed,
            failed ? result?.Text : null,
            family == ToolFamily.Shell && !failed ? result?.Text : null,
            result?.Line ?? use.Line);
    }

    private static JsonElement? Parse(string rawLine)
    {
        string line = rawLine.Trim();
        if (!line.StartsWith('{'))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return root.ValueKind != JsonValueKind.Object || ClaudeLiveProtocol.IsReplay(root) ? null : root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string type) =>
        root.TryGetProperty("message", out JsonElement message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out JsonElement content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && ClaudeBackend.TryGetString(item, "type") == type)
            : [];

    private static JsonElement Input(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement input) && input.ValueKind == JsonValueKind.Object ? input : default;

    // A tool_result's content is a string, or text items (an MCP tool's, a subagent's).
    private static string ContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out JsonElement content))
            return "";

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => string.Join('\n', content.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object)
                .Select(part => ClaudeBackend.TryGetString(part, "text"))
                .OfType<string>()),
            _ => "",
        };
    }

    private static string? PathOf(JsonElement input) => StringOf(input, "file_path") ?? StringOf(input, "path") ?? StringOf(input, "notebook_path");

    private static string? StringOf(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object ? ClaudeBackend.TryGetString(input, name) : null;
}
