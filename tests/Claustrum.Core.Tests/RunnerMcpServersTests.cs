using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Process;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// #128 at Runner level: what ClaudeBackend.Build decided about MCP servers is recorded in backend.json (`mcp_servers`) and its
// refusals are on the receipt. The backend is the real ClaudeBackend building the real argv; only the process it is pointed at
// is swapped for one that exits at once, so nothing here needs a claude install.
public sealed class RunnerMcpServersTests : IDisposable
{
    private const string Servers = /*lang=json,strict*/ """{"mcpServers":{"docs":{"command":"docs-mcp"}}}""";

    private readonly string homeDir = Directory.CreateTempSubdirectory("claustrum-home-").FullName;
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cwd-").FullName;

    public void Dispose()
    {
        TempTree.Delete(homeDir);
        TempTree.Delete(cwd);
    }

    // The real claude argv, run as a command that succeeds on either OS.
    private sealed class ClaudeBuildExitingAtOnce(HomeRedirectPlatform platform) : IBackend
    {
        private readonly ClaudeBackend claude = new(platform);

        public ProcessSpec? Built { get; private set; }

        public string Name => claude.Name;

        public Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken) => claude.DetectAsync(config, cancellationToken);

        public ProcessSpec Build(ResolvedRun run)
        {
            Built = claude.Build(run);
            string[] command = OperatingSystem.IsWindows() ? ["cmd", "/c", "exit 0"] : ["sh", "-c", "exit 0"];
            return Built with { Exe = command[0], Args = command[1..] };
        }

        public ParsedOutput Parse(string stdout, string stderr, int exitCode) => new("ok", "ses_1", null, null, [], null, IsError: false);
    }

    private async Task<(RunResult Result, ClaudeBuildExitingAtOnce Backend)> RunAsync(bool userMcp = false)
    {
        HomeRedirectPlatform platform = new(homeDir);
        ClaudeBuildExitingAtOnce backend = new(platform);
        Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
        RunRequest request = new(
            Role: "builder", Brief: "do it", BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
            Permission: null, BudgetUsd: null, Timeout: null, ResumeSession: null,
            AttachFiles: [], Env: [], Stream: false);
        ResolvedRole role = new("builder", "system prompt", "claude", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), Blind: false, HasReport: false, UserMcp: userMcp);

        RunResult result = await runner.RunAsync(request, role, new RunOptions(DiffByteCapBytes: 200_000), TestContext.Current.CancellationToken);
        return (result, backend);
    }

    private static JsonElement BackendJson(RunResult result, out JsonDocument document)
    {
        document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.LogPath)!, "backend.json")));
        return document.RootElement;
    }

    private static string[] ServerNames(JsonElement backendJson) =>
        [.. backendJson.GetProperty("mcp_servers").EnumerateArray().Select(server => server.GetString()!)];

    [Fact]
    public async Task BackendJsonListsTheServersOfTheReposMcpJsonAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(cwd, ".mcp.json"), Servers, TestContext.Current.CancellationToken);

        (RunResult result, ClaudeBuildExitingAtOnce backend) = await RunAsync();

        Assert.Equal(RunStatus.Success, result.Status);
        JsonElement json = BackendJson(result, out JsonDocument document);
        using (document)
        {
            Assert.Equal("claude", json.GetProperty("backend").GetString());
            Assert.Equal(["docs"], ServerNames(json));
        }

        Assert.Empty(result.Warnings);
        string copy = Path.Combine(Path.GetDirectoryName(result.LogPath)!, "mcp.json");
        Assert.Equal(copy, backend.Built!.Args[Array.IndexOf(backend.Built.Args, "--mcp-config") + 1]);
        Assert.Equal(Servers, await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackendJsonCarriesAnEmptyServerListWhenThereIsNoMcpJsonAsync()
    {
        (RunResult result, _) = await RunAsync();

        JsonElement json = BackendJson(result, out JsonDocument document);
        using (document)
            Assert.Equal([], ServerNames(json));

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task BackendJsonNamesTheUserServersWhenTheRoleOptedInAsync()
    {
        (RunResult result, ClaudeBuildExitingAtOnce backend) = await RunAsync(userMcp: true);

        JsonElement json = BackendJson(result, out JsonDocument document);
        using (document)
            Assert.Equal(["user"], ServerNames(json));

        Assert.DoesNotContain("--strict-mcp-config", backend.Built!.Args);
        Assert.Empty(result.Warnings);
    }

    // A backend that does not decide (every one but claude) leaves the key out altogether: absent is "not decided", [] is "none".
    [Fact]
    public async Task BackendJsonHasNoMcpServersKeyForABackendWhoseSpecLeavesThemNullAsync()
    {
        HomeRedirectPlatform platform = new(homeDir);
        ScriptedBackend backend = ScriptedBackend.Success();
        Runner runner = new(platform, new BackendRegistry([backend]), new ProcessRunner(platform));
        RunRequest request = new(
            Role: "builder", Brief: "do it", BriefFile: null, Cwd: cwd, Backend: null, Model: null, Effort: null,
            Permission: null, BudgetUsd: null, Timeout: null, ResumeSession: null,
            AttachFiles: [], Env: [], Stream: false);
        ResolvedRole role = new("builder", "system prompt", "scripted", "sonnet", "high", new PermissionPolicy(PermissionLevel.EditShell, []), Blind: false, HasReport: false);

        RunResult result = await runner.RunAsync(request, role, new RunOptions(DiffByteCapBytes: 200_000), TestContext.Current.CancellationToken);

        JsonElement json = BackendJson(result, out JsonDocument document);
        using (document)
        {
            Assert.Equal("scripted", json.GetProperty("backend").GetString());
            Assert.False(json.TryGetProperty("mcp_servers", out _));
        }
    }

    [Fact]
    public async Task ARefusedMcpJsonReachesTheReceiptAsExactlyOneWarningAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(cwd, ".mcp.json"), "{ not json", TestContext.Current.CancellationToken);

        (RunResult result, ClaudeBuildExitingAtOnce backend) = await RunAsync();

        Assert.Equal(RunStatus.Success, result.Status);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains(".mcp.json not given to claude", warning, StringComparison.Ordinal);
        Assert.Contains("--strict-mcp-config", backend.Built!.Args);
        Assert.DoesNotContain("--mcp-config", backend.Built.Args);
        JsonElement json = BackendJson(result, out JsonDocument document);
        using (document)
            Assert.Equal([], ServerNames(json));
    }

    [Fact]
    public async Task ALinkLeavingTheRepoReachesTheReceiptAsExactlyOneWarningAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("creating a symlink needs a privilege a Windows runner does not always grant.");

        string outside = Path.Combine(homeDir, "elsewhere.json");
        await File.WriteAllTextAsync(outside, Servers, TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(cwd, ".mcp.json"), outside);

        (RunResult result, ClaudeBuildExitingAtOnce backend) = await RunAsync();

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("outside the working directory", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcp-config", backend.Built!.Args);
    }
}
