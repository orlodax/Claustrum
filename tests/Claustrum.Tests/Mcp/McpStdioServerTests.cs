using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Mcp;

// Review finding #11 — the one only a real host reproduces. `delegate` hung forever because every
// spawned child inherited the MCP server's own stdin: the live JSON-RPC pipe the host writes into and
// the stdio transport concurrently reads. A probe whose stdin hits EOF (`echo … | claustrum mcp`)
// tears the server down before the hang can happen, which is exactly how the PR that shipped the bug
// passed its own manual test — so McpStdioClient keeps stdin open, as every real host does.
// See NOTES.md "MCP child stdin inheritance hung git".
public sealed class McpStdioServerTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-mcp-stdio-home-").FullName;
    private readonly string work = Directory.CreateTempSubdirectory("claustrum-mcp-stdio-cwd-").FullName;

    public void Dispose()
    {
        Directory.Delete(home, recursive: true);
        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task DelegateOverRealStdioReturnsInsteadOfHangingOnTheHostsPipeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using McpStdioClient client = StartServer();
        await InitializeAsync(client, ct);

        JsonNode? response = await client.RequestAsync("tools/call", new JsonObject
        {
            ["name"] = "delegate",
            ["arguments"] = new JsonObject
            {
                ["role"] = "builder",
                ["brief"] = "## Task\ndo nothing",
                ["cwd"] = work,
                // Registered, so the run reaches WorktreeSnapshot's `git status` — the spawn that
                // hung — before BinaryLocator rejects it. PATH is stripped to git's own directory
                // (StartServer), so no real backend can be invoked from a test.
                ["backend"] = "claude",
            },
        }, ct);

        // Not merely "a response arrived": RunGitAsync's defensive 30s timeout means the unfixed code
        // answers eventually too, with isError and "An error occurred invoking 'delegate'" (measured
        // 2026-09-14: 30.16s unfixed, 0.20s fixed). Asserting the payload is what makes this fail.
        Assert.NotNull(response);
        Assert.False(response!["result"]!["isError"]?.GetValue<bool>() ?? false, $"{response.ToJsonString()}\n{client.ServerLog}");

        using JsonDocument result = JsonDocument.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("backend_missing", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ToolsListOverRealStdioAdvertisesEveryPlannedToolAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using McpStdioClient client = StartServer();
        await InitializeAsync(client, ct);

        JsonNode? response = await client.RequestAsync("tools/list", [], ct);

        Assert.NotNull(response);
        string[] names = [.. response!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>())];
        Assert.Equal(10, names.Length);
        Assert.Contains("delegate", names);
        Assert.Contains("job_status", names);
    }

    private static async Task InitializeAsync(McpStdioClient client, CancellationToken cancellationToken)
    {
        JsonNode? handshake = await client.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "claustrum-tests", ["version"] = "1" },
        }, cancellationToken);

        Assert.NotNull(handshake);
        client.Notify("notifications/initialized");
    }

    private McpStdioClient StartServer()
    {
        string binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "claustrum.exe" : "claustrum");
        Assert.True(File.Exists(binary), $"built claustrum binary not found at '{binary}'");

        ProcessStartInfo startInfo = new()
        {
            FileName = binary,
            WorkingDirectory = work,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(work);
        startInfo.Environment["CLAUSTRUM_HOME"] = home;
        startInfo.Environment["PATH"] = GitDirectory();

        return new McpStdioClient(Process.Start(startInfo)!);
    }

    // git has to stay reachable (the snapshot is the point); nothing else may be, so a `claude` that
    // happens to be installed on the developer's machine cannot be invoked by this test.
    private static string GitDirectory()
    {
        string executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable)))
                return directory;
        }

        throw new InvalidOperationException("git was not found on PATH; the MCP stdio tests need it");
    }
}
