using System.Text;
using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends.Api;

// docs/PLAN.md §A3 "api" row: system message = role body, user = brief, no tools — the only backend
// with no filesystem/shell access at all, which is why only text-report roles list it in their
// role.json harnesses (code-reviewer/ui-reviewer), never builder/tester. `curl` is the process this
// backend actually spawns: a raw HTTP POST needs no SDK, and writing the request body plus the auth
// header to temp files (`-d @file`, `-K` config) instead of argv keeps the API key and a potentially
// large brief out of `ps` — both files are in ProcessSpec.TempFiles, so Runner deletes them in its
// `finally` regardless of outcome (same guarantee ClaudeBackend's job files rely on).
public sealed class ApiBackend(IPlatform platform) : IBackend
{
    private static readonly TimeSpan detectTimeout = TimeSpan.FromSeconds(10);

    // Anthropic's Messages API requires max_tokens; there is no per-role token budget in Claustrum
    // to derive one from (BudgetUsd is a dollar cap, not a token count), so this stays a generous
    // fixed ceiling. Still unmeasured on 2026-09-22 and recorded as such: no ANTHROPIC_API_KEY
    // exists on this machine and the endpoint answers 401 before it looks at the body, so nothing
    // short of a real key can tell whether 8192 is too low for a role — NOTES.md "The opencode and
    // api backends, validated against real endpoints" lists what such a pass would have to do.
    private const int AnthropicMaxTokens = 8192;

    public string Name => "api";

    public async Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken)
    {
        Doctor curl = await VersionProbe.RunAsync("curl", ["--version"], config, platform, cancellationToken, detectTimeout);
        if (!curl.Found)
            return curl;

        bool hasKey = !string.IsNullOrEmpty(platform.GetEnvironmentVariable("OPENROUTER_API_KEY"))
            || !string.IsNullOrEmpty(platform.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

        return hasKey ? curl : curl with { Problems = [.. curl.Problems, "neither OPENROUTER_API_KEY nor ANTHROPIC_API_KEY is set"] };
    }

    public ProcessSpec Build(ResolvedRun run)
    {
        int colon = run.Role.Model.IndexOf(':');
        if (colon < 0)
            throw new InvalidOperationException($"api backend model must be 'openrouter:<model>' or 'anthropic:<model>', got '{run.Role.Model}'");

        string provider = run.Role.Model[..colon];
        string modelId = run.Role.Model[(colon + 1)..];
        string systemPrompt = File.ReadAllText(run.SystemPromptFilePath);

        (string url, string body, string authHeader) = provider switch
        {
            "openrouter" => (
                "https://openrouter.ai/api/v1/chat/completions",
                BuildOpenAiCompatibleBody(modelId, systemPrompt, run.Brief),
                $"Authorization: Bearer {RequireKey("OPENROUTER_API_KEY")}"),
            "anthropic" => (
                "https://api.anthropic.com/v1/messages",
                BuildAnthropicBody(modelId, systemPrompt, run.Brief),
                $"x-api-key: {RequireKey("ANTHROPIC_API_KEY")}"),
            _ => throw new InvalidOperationException($"api backend model provider must be 'openrouter' or 'anthropic', got '{provider}'"),
        };

        string bodyPath = Path.Combine(run.JobDirectory, "api-request-body.json");
        string configPath = Path.Combine(run.JobDirectory, "api-curl-config");
        File.WriteAllText(bodyPath, body);
        WritePrivate(configPath, BuildCurlConfig(authHeader, provider));

        // -q FIRST: without it curl also reads ~/.curlrc, and one `output = …`/`proxy = …` line
        // there silently redirects the response — Parse then sees empty stdout and reports a
        // failure with nothing to explain it. The spawn has to be hermetic for the same reason
        // ProcessRunner clears the inherited environment (review finding).
        string[] args = ["-q", "--fail-with-body", "--silent", "--show-error", "-K", configPath, "-d", $"@{bodyPath}", url];
        return new ProcessSpec("curl", args, run.Cwd, run.Env, [bodyPath, configPath]);
    }

    public ParsedOutput Parse(string stdout, string stderr, int exitCode)
    {
        string trimmed = stdout.Trim();
        if (trimmed.Length == 0)
            return new ParsedOutput(stderr, null, null, null, [], null, IsError: true);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return new ParsedOutput(trimmed, null, null, null, [], null, IsError: exitCode != 0);
        }

        using (document)
        {
            JsonElement root = document.RootElement.Clone();

            // Checked before dispatching on success shape: an error body from either provider has
            // no "choices"/"content" array of its own, so without this an OpenRouter error would
            // fall through to the Anthropic branch (and vice versa) and report an empty message.
            // Presence alone is not the test — see IsRealError.
            if (root.TryGetProperty("error", out JsonElement errorElement) && IsRealError(errorElement))
            {
                string message = errorElement.ValueKind == JsonValueKind.Object && errorElement.TryGetProperty("message", out JsonElement messageProp)
                    ? messageProp.GetString() ?? trimmed
                    : trimmed;
                return new ParsedOutput(message, null, null, null, [], root, IsError: true);
            }

            return root.TryGetProperty("choices", out JsonElement choices)
                ? ParseOpenRouterSuccess(root, choices, exitCode != 0)
                : ParseAnthropicSuccess(root, exitCode != 0);
        }
    }

    // An `error` property is a failure only when it carries something. OpenRouter emits a JSON
    // **null** for an optional field it has nothing to say about (`service_tier`, `refusal` in the
    // genuine 2026-09-22 success capture), so testing presence alone would report a billed, correct
    // response as Failed with the whole body as its message — the same trap `"usage": null` sprang
    // one level down, and the same ValueKind discipline the success parsers now use.
    private static bool IsRealError(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.Object => true,
        JsonValueKind.String => error.GetString() is { Length: > 0 },
        _ => false,
    };

    private string RequireKey(string envVarName) =>
        platform.GetEnvironmentVariable(envVarName) is { Length: > 0 } key
            ? key
            : throw new InvalidOperationException($"api backend needs {envVarName} set");

    // The curl config file holds the API key in clear text, so it is created owner-only before a
    // byte of it is written — File.WriteAllText would have left it 0644 under the default umask,
    // world-readable for the life of the run in ~/.claustrum/jobs/<id>/ (review finding). Keeping
    // the key off argv is only half the job if the file it moves to is readable by everyone.
    // UnixFileMode is a no-op on Windows, where the job directory inherits the user profile's ACL.
    private static void WritePrivate(string path, string contents)
    {
        FileStreamOptions options = new() { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };

        // Setting UnixCreateMode at all throws on Windows (CA1416), where the job directory already
        // inherits the user profile's ACL and no other account can read it anyway.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using StreamWriter writer = new(path, options);
        writer.Write(contents);
    }

    // curl's -K/--config file syntax: bare long-option name, "= value" (quoted so the ": " inside an
    // HTTP header value isn't mistaken for another option). Kept out of argv/`ps` unlike -H would be.
    private static string BuildCurlConfig(string authHeaderLine, string provider)
    {
        StringBuilder config = new();
        config.Append("header = \"").Append(authHeaderLine).Append("\"\n");
        config.Append("header = \"Content-Type: application/json\"\n");
        if (provider == "anthropic")
            config.Append("header = \"anthropic-version: 2023-06-01\"\n");
        return config.ToString();
    }

    private static string BuildOpenAiCompatibleBody(string modelId, string systemPrompt, string brief)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", systemPrompt);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", brief);
            writer.WriteEndObject();
            writer.WriteEndArray();
            // OpenRouter's documented opt-in for usage accounting. Measured 2026-09-22: this account
            // gets `usage.cost` on deepseek-v4-flash *with or without* it, so it is belt-and-braces
            // rather than load-bearing — kept because "one account, one model" is no basis for
            // dropping the only documented way to ask.
            writer.WriteStartObject("usage");
            writer.WriteBoolean("include", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildAnthropicBody(string modelId, string systemPrompt, string brief)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteNumber("max_tokens", AnthropicMaxTokens);
            writer.WriteString("system", systemPrompt);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", brief);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ParsedOutput ParseOpenRouterSuccess(JsonElement root, JsonElement choices, bool httpError)
    {
        string finalMessage = choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement content)
            ? content.GetString() ?? ""
            : "";

        Usage? usage = null;
        decimal? cost = null;
        // `ValueKind == Object`, not just "present": OpenRouter emits a JSON **null** for an
        // optional field, and `TryGetProperty` on a null element throws — which turned a billed,
        // successful response into a Failed run out of `Parse`. Same guard `TryGetInt` already has.
        if (root.TryGetProperty("usage", out JsonElement usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            // All four names read off a genuine 2026-09-22 capture (tests/fixtures/api/
            // openrouter-success.json): `usage.cost` is the dollar figure, and the cache counts live
            // one level down in `prompt_tokens_details`, which this used to leave null.
            JsonElement promptDetails = usageElement.TryGetProperty("prompt_tokens_details", out JsonElement details) ? details : default;
            usage = new Usage(
                TryGetInt(usageElement, "prompt_tokens"),
                TryGetInt(usageElement, "completion_tokens"),
                TryGetInt(promptDetails, "cached_tokens"),
                TryGetInt(promptDetails, "cache_write_tokens"));
            cost = usageElement.TryGetProperty("cost", out JsonElement costProp) && costProp.TryGetDecimal(out decimal costValue) ? costValue : null;
        }

        return new ParsedOutput(finalMessage, null, cost, usage, [], root, httpError);
    }

    private static ParsedOutput ParseAnthropicSuccess(JsonElement root, bool httpError)
    {
        string finalMessage = "";
        if (root.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            StringBuilder text = new();
            foreach (JsonElement block in content.EnumerateArray())
                if (block.TryGetProperty("type", out JsonElement typeProp) && typeProp.GetString() == "text"
                    && block.TryGetProperty("text", out JsonElement textProp))
                    text.Append(textProp.GetString());
            finalMessage = text.ToString();
        }

        Usage? usage = root.TryGetProperty("usage", out JsonElement usageElement) && usageElement.ValueKind == JsonValueKind.Object
            ? new Usage(TryGetInt(usageElement, "input_tokens"), TryGetInt(usageElement, "output_tokens"), null, null)
            : null;

        return new ParsedOutput(finalMessage, null, null, usage, [], root, httpError);
    }

    private static int? TryGetInt(JsonElement parent, string propertyName) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(propertyName, out JsonElement property)
        && property.TryGetInt32(out int value) ? value : null;
}
