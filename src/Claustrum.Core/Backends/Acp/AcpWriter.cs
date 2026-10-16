using System.Text;
using System.Text.Json;

namespace Claustrum.Core.Backends.Acp;

// Every frame the client sends, as one line of JSON without its line break (the writer adds it), written the way
// ClaudeLiveProtocol.EncodeUserMessage writes claude's: Utf8JsonWriter, no serializer, nothing for AOT to trim. The client
// advertises neither `fs` nor `terminal`, so the agent runs its own tools and asks the client for nothing but permissions.
internal static class AcpWriter
{
    private const int ProtocolVersion = 1;

    public static string Initialize(string id, string clientVersion) => Request(id, "initialize", writer =>
    {
        writer.WriteNumber("protocolVersion", ProtocolVersion);
        writer.WriteStartObject("clientCapabilities");
        writer.WriteStartObject("fs");
        writer.WriteBoolean("readTextFile", false);
        writer.WriteBoolean("writeTextFile", false);
        writer.WriteEndObject();
        writer.WriteBoolean("terminal", false);
        writer.WriteEndObject();
        writer.WriteStartObject("clientInfo");
        writer.WriteString("name", "claustrum");
        writer.WriteString("version", clientVersion);
        writer.WriteEndObject();
    });

    public static string NewSession(string id, string cwd) => Request(id, "session/new", writer =>
    {
        writer.WriteString("cwd", cwd);
        writer.WriteStartArray("mcpServers");
        writer.WriteEndArray();
    });

    public static string LoadSession(string id, string sessionId, string cwd) => Request(id, "session/load", writer =>
    {
        writer.WriteString("sessionId", sessionId);
        writer.WriteString("cwd", cwd);
        writer.WriteStartArray("mcpServers");
        writer.WriteEndArray();
    });

    public static string SetConfigOption(string id, string sessionId, string configId, string value) => Request(id, "session/set_config_option", writer =>
    {
        writer.WriteString("sessionId", sessionId);
        writer.WriteString("configId", configId);
        writer.WriteString("value", value);
    });

    public static string Prompt(string id, string sessionId, string text) => Request(id, "session/prompt", writer =>
    {
        writer.WriteString("sessionId", sessionId);
        writer.WriteStartArray("prompt");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
    });

    public static string PermissionSelected(JsonElement id, string optionId) => Response(id, writer =>
    {
        writer.WriteStartObject("result");
        writer.WriteStartObject("outcome");
        writer.WriteString("outcome", "selected");
        writer.WriteString("optionId", optionId);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // The schema's one answer that selects nothing: for a call the deny list refuses when the agent offered no reject option.
    public static string PermissionCancelled(JsonElement id) => Response(id, writer =>
    {
        writer.WriteStartObject("result");
        writer.WriteStartObject("outcome");
        writer.WriteString("outcome", "cancelled");
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string MethodNotFound(JsonElement id, string method) => Response(id, writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", -32601);
        writer.WriteString("message", $"client does not implement {method}");
        writer.WriteEndObject();
    });

    private static string Request(string id, string method, Action<Utf8JsonWriter> writeParams) => Frame(writer =>
    {
        writer.WriteString("id", id);
        writer.WriteString("method", method);
        writer.WriteStartObject("params");
        writeParams(writer);
        writer.WriteEndObject();
    });

    // The agent's own request id goes back exactly as it came, number or string.
    private static string Response(JsonElement id, Action<Utf8JsonWriter> writeBody) => Frame(writer =>
    {
        writer.WritePropertyName("id");
        id.WriteTo(writer);
        writeBody(writer);
    });

    private static string Frame(Action<Utf8JsonWriter> writeBody)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
