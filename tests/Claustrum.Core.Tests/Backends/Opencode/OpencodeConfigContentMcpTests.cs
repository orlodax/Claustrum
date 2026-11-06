using System.Text.Json;
using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Opencode;

// #135: a browser role's Playwright server rides in the run's own OPENCODE_CONFIG_CONTENT, never the repo's
// opencode.json — OpencodeBackend.BuildConfigContent writes an `mcp` section only when the role names a
// server. These drive the public Build path, so they pin exactly what `opencode run` is handed.
public sealed class OpencodeConfigContentMcpTests
{
    private readonly OpencodeBackend backend = new(new FakePlatform());

    private static ResolvedRun MakeRun(string[]? mcpServers) => new(
        Role: new ResolvedRole(
            "ui-reviewer", "system body", "opencode", "openrouter/deepseek/deepseek-v4-pro", "high",
            new PermissionPolicy(PermissionLevel.Shell, []), Blind: false, HasReport: true, McpServers: mcpServers),
        Brief: "do the thing",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: "/job/system.md",
        JobDirectory: "/job",
        Env: []);

    private JsonDocument Config(string[]? mcpServers) =>
        JsonDocument.Parse(backend.Build(MakeRun(mcpServers)).Env["OPENCODE_CONFIG_CONTENT"]);

    [Fact]
    public void NoMcpSectionWhenTheRoleNamesNoServer()
    {
        using JsonDocument config = Config(null);

        Assert.False(config.RootElement.TryGetProperty("mcp", out _));
    }

    [Fact]
    public void AnEmptyServerListAlsoWritesNoMcpSection()
    {
        using JsonDocument config = Config([]);

        Assert.False(config.RootElement.TryGetProperty("mcp", out _));
    }

    [Fact]
    public void APlaywrightServerIsRegisteredAsALocalNpxCommand()
    {
        using JsonDocument config = Config(["playwright"]);

        JsonElement playwright = config.RootElement.GetProperty("mcp").GetProperty("playwright");
        Assert.Equal("local", playwright.GetProperty("type").GetString());
        Assert.Equal(
            ["npx", "@playwright/mcp@0.0.84", "--headless", "--isolated"],
            playwright.GetProperty("command").EnumerateArray().Select(part => part.GetString()!));
    }

    // Only `playwright` is a server claustrum knows how to spell today: any other name is skipped.
    [Fact]
    public void AnUnknownServerNameIsSkipped()
    {
        using JsonDocument config = Config(["not-a-known-server"]);

        Assert.Empty(config.RootElement.GetProperty("mcp").EnumerateObject());
    }

    [Fact]
    public void AKnownAndAnUnknownServerLeaveOnlyTheKnownOne()
    {
        using JsonDocument config = Config(["playwright", "not-a-known-server"]);

        Assert.Equal(["playwright"], config.RootElement.GetProperty("mcp").EnumerateObject().Select(server => server.Name));
    }
}
