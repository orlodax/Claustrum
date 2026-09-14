using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Claustrum.Tests.Testing;

/// <summary>
/// A minimal newline-delimited JSON-RPC client for <c>claustrum mcp</c>, for McpStdioServerTests.
/// Deliberately keeps the server's stdin pipe OPEN for the process's whole lifetime — that live pipe
/// is the whole point of the fixture (review finding #11) — and drains stdout and stderr on their own
/// tasks so neither pipe can fill and stall the server.
/// </summary>
internal sealed class McpStdioClient : IAsyncDisposable
{
    private static readonly TimeSpan responseTimeout = TimeSpan.FromSeconds(60);

    private readonly Process process;
    private readonly Lock gate = new();
    private readonly Dictionary<int, JsonNode> responses = [];
    private readonly StringBuilder log = new();
    private readonly Task stdoutPump;
    private readonly Task stderrPump;
    private int nextId;

    public McpStdioClient(Process process)
    {
        this.process = process;
        stdoutPump = Task.Run(PumpStdoutAsync);
        stderrPump = Task.Run(PumpStderrAsync);
    }

    public string ServerLog
    {
        get
        {
            lock (gate)
                return log.ToString();
        }
    }

    public void Notify(string method) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = new JsonObject() });

    /// <summary>Returns null if the server never answered within the timeout — i.e. it hung.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref nextId);
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

        DateTime deadline = DateTime.UtcNow + responseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (gate)
            {
                if (responses.Remove(id, out JsonNode? response))
                    return response;
            }

            if (process.HasExited)
                throw new InvalidOperationException($"the MCP server exited (code {process.ExitCode}) before answering '{method}':\n{ServerLog}");

            await Task.Delay(50, cancellationToken);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);

        await Task.WhenAll(stdoutPump, stderrPump);
        process.Dispose();
    }

    private void Send(JsonObject message)
    {
        process.StandardInput.WriteLine(message.ToJsonString());
        process.StandardInput.Flush();
    }

    private async Task PumpStdoutAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (JsonNode.Parse(line) is not JsonObject message || message["id"]?.GetValue<int>() is not { } id)
                continue;

            lock (gate)
                responses[id] = message;
        }
    }

    private async Task PumpStderrAsync()
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            lock (gate)
                log.AppendLine(line);
        }
    }
}
