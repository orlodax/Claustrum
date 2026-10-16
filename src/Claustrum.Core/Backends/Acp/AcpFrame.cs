using System.Globalization;
using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// One JSON-RPC 2.0 frame of an ACP transcript, its elements cloned out of the document it was read from. A response carries the id of
// the request it answers; the client's ids are `<tag>-<n>` (AcpRequestId), so a response says by itself what it answers.
internal sealed record AcpFrame(JsonElement? Id, string? Method, JsonElement? Params, JsonElement? Result, JsonElement? Error)
{
    public bool IsResponse => Id is not null && Method is null;

    public bool IsRequest => Id is not null && Method is not null;

    public bool IsNotification => Id is null && Method is not null;

    public string? IdText => Id is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;

    /// <summary>The tag of one of this client's request ids (<c>prompt</c> for <c>prompt-10</c>); null for a numeric id.</summary>
    public string? IdTag => IdText is { } text && text.LastIndexOf('-') is > 0 and int dash ? text[..dash] : null;

    // What a response answers: the tag of this client's id, or, for an id an agent did not hand back as sent, the result's shape
    // (a stop reason is a prompt's, a session id a new session's). A `session/load` and a `set_config_option` look alike: tag only.
    public string? Answers => IdTag is AcpRequestId.Init or AcpRequestId.New or AcpRequestId.Load or AcpRequestId.Config or AcpRequestId.Prompt
        ? IdTag
        : Result is not { ValueKind: JsonValueKind.Object } result ? null
        : result.TryGetProperty("stopReason", out _) ? AcpRequestId.Prompt
        : result.TryGetProperty("sessionId", out _) ? AcpRequestId.New
        : result.TryGetProperty("protocolVersion", out _) ? AcpRequestId.Init
        : null;

    /// <summary>Null for a line that is not a JSON object: never a throw, whatever the agent printed.</summary>
    public static AcpFrame? TryParse(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return new AcpFrame(
                Element(root, "id") is { ValueKind: JsonValueKind.String or JsonValueKind.Number } id ? id : null,
                AcpJson.StringOf(root, "method"),
                Element(root, "params"),
                Element(root, "result"),
                AcpJson.ObjectOf(root, "error")?.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Element(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null ? element.Clone() : null;
}

// The client's request ids: `<tag>-<n>`, the tag naming the method, n counting every request of the run. They round-trip verbatim
// (measured on opencode 2.0.30, 2026-10-16), which is what makes stdout.log readable without the client's half.
internal static class AcpRequestId
{
    public const string Init = "init";
    public const string New = "new";
    public const string Load = "load";
    public const string Config = "config";
    public const string Prompt = "prompt";

    public static string Make(string tag, int n) => $"{tag}-{n.ToString(CultureInfo.InvariantCulture)}";
}
