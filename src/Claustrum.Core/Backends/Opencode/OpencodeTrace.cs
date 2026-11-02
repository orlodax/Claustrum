using System.Text.Json;
using Claustrum.Core.Tracing;

namespace Claustrum.Core.Backends.Opencode;

// #145: a native `opencode run --format json` run's tool use, for EnvironmentClassifier: each `tool_use` event's part (`tool`, and
// `state.{status, input, output, error}`; status `error` is a call that failed), the last event of a call id being the one that counts,
// as in Parse. Shapes: tests/fixtures/opencode (2.0.12) and NOTES.md's `{"status":"error","error":"Permission denied: read"}`.
internal static class OpencodeTrace
{
    public static RunTrace Read(string stdout)
    {
        Dictionary<string, TracedCall> calls = [];
        List<string> order = [];
        string[] lines = stdout.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            if (CallOf(lines[index], index + 1) is not { } call)
                continue;

            if (!calls.ContainsKey(call.Id))
                order.Add(call.Id);
            calls[call.Id] = call.Traced;
        }

        return new RunTrace([.. order.Select(id => calls[id])], []);
    }

    private static (string Id, TracedCall Traced)? CallOf(string rawLine, int number)
    {
        string line = rawLine.Trim();
        if (!line.StartsWith('{'))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || StringOf(root, "type") != "tool_use"
                || ObjectOf(root, "part") is not { } part || StringOf(part, "id") is not { } id)
            {
                return null;
            }

            string name = StringOf(part, "tool") ?? "";
            JsonElement state = ObjectOf(part, "state") ?? default;
            JsonElement input = ObjectOf(state, "input") ?? default;
            ToolFamily family = ToolNames.FamilyOf(name);
            bool failed = StringOf(state, "status") == "error";
            string? path = family == ToolFamily.Shell ? StringOf(input, "workdir") : StringOf(input, "filePath") ?? StringOf(input, "path");
            return (id, new TracedCall(
                name,
                family,
                StringOf(input, "command"),
                path,
                StringOf(input, "url"),
                failed,
                failed ? StringOf(state, "error") : null,
                family == ToolFamily.Shell ? StringOf(state, "output") : null,
                number,
                ExitOf(ObjectOf(state, "metadata") ?? default)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The bash tool's `metadata.exit`, as the ACP transport carries it (not seen in a native capture: none holds a shell call).
    private static int? ExitOf(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("exit", out JsonElement exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) ? code : null;

    private static JsonElement? ObjectOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? StringOf(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
