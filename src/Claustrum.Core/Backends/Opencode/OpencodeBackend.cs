using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Opencode;

// docs/PLAN.md §A3 "opencode" row, verified against the real `opencode-ai` npm package (v1.18.31,
// installed and probed 2026-09-18 — see NOTES.md "The opencode backend" for exactly what was
// confirmed live vs. inferred from its published SDK types). Confirmed live: `OPENCODE_CONFIG_CONTENT`
// (with `{file:<absolute path>}` substitution) and the separate, simpler `OPENCODE_PERMISSION` env var
// both really exist and are read by the binary; `--variant` is the effort/reasoning-tier flag (not
// guessed in the original plan); `run --format json` emits one JSON object per line, each carrying
// `type` and `sessionID`; a `type:"error"` event with a real captured shape means the run failed
// (recorded in tests/fixtures/opencode/error.jsonl, not fabricated). NOT independently confirmed:
// the exact shape of a successful run's `message.part.updated`/`step-finish` events — inferred from
// opencode's public SDK types and the event *names* found as literal strings in the binary, since no
// working provider credential was available to record a real success.
public sealed class OpencodeBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    public string Name => "opencode";

    public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) =>
        VersionProbe.RunAsync(Name, ["--version"], config, platform, cancellationToken, detectTimeout);

    public ProcessSpec Build(ResolvedRun run)
    {
        string agentName = $"claustrum-{run.Role.Name}";
        string configContent = BuildConfigContent(agentName, run.SystemPromptFilePath);
        string permissionJson = BuildPermissionJson(run.Role.Permission);

        List<string> args = ["run", "--agent", agentName, "--model", run.Role.Model, "--format", "json", "--dir", run.Cwd];
        if (!string.IsNullOrEmpty(run.Role.Effort))
            args.AddRange(["--variant", run.Role.Effort]);
        if (run.Role.Permission.Level == PermissionLevel.Full)
            args.Add("--auto");
        if (run.ResumeSession is { Length: > 0 } resumeSession)
            args.AddRange(["--session", resumeSession]);
        args.Add(run.Brief);

        Dictionary<string, string> env = new(run.Env, StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG_CONTENT"] = configContent,
            ["OPENCODE_PERMISSION"] = permissionJson,
        };

        return new ProcessSpec(Name, [.. args], run.Cwd, env, []);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: exitCode != 0);

        string? sessionId = null;
        Dictionary<string, (int Order, string Text)> textParts = [];
        decimal? cost = null;
        Usage? usage = null;
        string? errorMessage = null;
        JsonElement? lastEvent = null;
        int nextOrder = 0;

        foreach (string rawLine in trimmed.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                lastEvent = root.Clone();
                if (root.TryGetProperty("sessionID", out JsonElement sessionProp))
                    sessionId = sessionProp.GetString();

                string? type = root.TryGetProperty("type", out JsonElement typeProp) ? typeProp.GetString() : null;
                if (type == "error")
                {
                    errorMessage = ExtractErrorMessage(root, line);
                    continue;
                }

                if (type != "message.part.updated" || !root.TryGetProperty("part", out JsonElement part))
                    continue;

                string? partId = part.TryGetProperty("id", out JsonElement idProp) ? idProp.GetString() : null;
                string? partType = part.TryGetProperty("type", out JsonElement partTypeProp) ? partTypeProp.GetString() : null;

                // "updated" carries the part's full current text, not an incremental chunk (there is
                // a separate "message.part.delta" event for that) — later updates for the same part
                // id replace, rather than append to, its recorded text.
                if (partType == "text" && partId is not null && part.TryGetProperty("text", out JsonElement textProp))
                {
                    int order = textParts.TryGetValue(partId, out (int Order, string Text) existing) ? existing.Order : nextOrder++;
                    textParts[partId] = (order, textProp.GetString() ?? "");
                }
                else if (partType == "step-finish")
                {
                    ApplyStepFinish(part, ref cost, ref usage);
                }
            }
        }

        string finalMessage = string.Join("", textParts.OrderBy(entry => entry.Value.Order).Select(entry => entry.Value.Text));
        if (finalMessage.Length == 0 && errorMessage is not null)
            finalMessage = errorMessage;

        return new ParsedOutput(finalMessage, sessionId, cost, usage, [], lastEvent, IsError: exitCode != 0);
    }

    private static string ExtractErrorMessage(JsonElement root, string rawLine) =>
        root.TryGetProperty("error", out JsonElement errorElement)
        && errorElement.TryGetProperty("data", out JsonElement dataElement)
        && dataElement.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement.GetString() ?? rawLine
            : rawLine;

    private static void ApplyStepFinish(JsonElement part, ref decimal? cost, ref Usage? usage)
    {
        if (part.TryGetProperty("cost", out JsonElement costProp) && costProp.TryGetDecimal(out decimal costValue))
            cost = costValue;

        if (!part.TryGetProperty("tokens", out JsonElement tokens))
            return;

        int? cacheRead = null;
        int? cacheWrite = null;
        if (tokens.TryGetProperty("cache", out JsonElement cache))
        {
            cacheRead = TryGetInt(cache, "read");
            cacheWrite = TryGetInt(cache, "write");
        }

        usage = new Usage(TryGetInt(tokens, "input"), TryGetInt(tokens, "output"), cacheRead, cacheWrite);
    }

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement property) && property.TryGetInt32(out int value) ? value : null;

    // `{file:<path>}` substitution inside OPENCODE_CONFIG_CONTENT is real (confirmed live): opencode
    // reads the referenced file's contents in place of the placeholder, so the rendered role body
    // never has to be JSON-escaped by hand the way an inline string would.
    private static string BuildConfigContent(string agentName, string systemPromptFilePath)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("agent");
            writer.WriteStartObject(agentName);
            writer.WriteString("mode", "primary");
            writer.WriteString("prompt", $"{{file:{systemPromptFilePath}}}");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // docs/PLAN.md §A3 permission table's opencode column, minus Full (handled by --auto in Build,
    // which approves anything not explicitly denied — no JSON needed for that level, though sending
    // an allow-all permission object alongside --auto is harmless).
    private static string BuildPermissionJson(PermissionPolicy permission)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            switch (permission.Level)
            {
                case PermissionLevel.ReadOnly:
                    writer.WriteString("edit", "deny");
                    writer.WriteString("bash", "deny");
                    break;
                case PermissionLevel.Edit:
                    writer.WriteString("edit", "allow");
                    writer.WriteString("bash", "deny");
                    break;
                case PermissionLevel.EditShell:
                    writer.WriteString("edit", "allow");
                    writer.WriteStartObject("bash");
                    writer.WriteString("*", "allow");
                    foreach (string pattern in permission.Deny)
                        writer.WriteString($"{pattern}*", "deny");
                    writer.WriteEndObject();
                    break;
                case PermissionLevel.Full:
                    writer.WriteString("edit", "allow");
                    writer.WriteString("bash", "allow");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(permission));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
