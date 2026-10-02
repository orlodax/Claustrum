using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Core.Jobs;
using Claustrum.Mcp;
using Claustrum.Tests.Delegation;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// The MCP half of #43's harness check: DelegateEngine.RequireSupportedHarness throws a
// RoleRenderException, McpExceptionBoundary turns it into an McpException carrying the real message, and
// no job is minted — delegate, delegate_async and coordinate all prepare before JobDirectory.Create. The
// same check guards cast_create, so a cast that could never run is not saved. No backend is started: every
// call here throws (or, for the saved cast, only writes a file).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class HarnessRefusalToolsTests(AppServicesHomeFixture fixture) : IDisposable
{
    private const string Sources = "--backend/--model, the cast or claustrum.json";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-harness-").FullName;

    public void Dispose() => TempTree.Delete(cwd);

    [Theory]
    [MemberData(nameof(HarnessRefusalTests.BrowserRolesOnOtherHarnesses), MemberType = typeof(HarnessRefusalTests))]
    public async Task DelegateRefusesABrowserRoleOnAnotherHarnessAndMintsNoJobAsync(string role, string backend)
    {
        string[] before = JobDirectories();

        McpException exception = await Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.DelegateAsync(role: role, brief: "hi", cwd: cwd, backend: backend, cancellationToken: TestContext.Current.CancellationToken));

        AssertRefusal(exception, role, backend);
        Assert.Equal(before, JobDirectories());
    }

    [Theory]
    [MemberData(nameof(HarnessRefusalTests.BrowserRolesOnOtherHarnesses), MemberType = typeof(HarnessRefusalTests))]
    public void DelegateAsyncRefusesOnTheCallItselfAndStartsNoJob(string role, string backend)
    {
        string[] before = JobDirectories();

        McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.DelegateStart(role: role, brief: "hi", cwd: cwd, backend: backend));

        AssertRefusal(exception, role, backend);
        Assert.Equal(before, JobDirectories());
    }

    // A model routed through the cast reaches the same check: delegate carries no backend flag here.
    [Fact]
    public async Task DelegateRefusesAModelThatTheCastRoutesToAnotherHarnessAsync()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Host),
            new Dictionary<string, CastRoleEntry?> { ["demo-author"] = new CastRoleEntry(Model: "opencode:some-model", Backend: null, Tier: null) },
            BudgetUsd: null));

        McpException exception = await Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.DelegateAsync(role: "demo-author", brief: "hi", cwd: cwd, cancellationToken: TestContext.Current.CancellationToken));

        AssertRefusal(exception, "demo-author", "opencode");
    }

    [Fact]
    public async Task CoordinateRefusesAnArchitectTheCastSpawnsOnApiAndMintsNoJobAsync()
    {
        CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "api:some-model"), [], null));
        string[] before = JobDirectories();

        McpException exception = await Assert.ThrowsAsync<McpException>(
            () => ClaustrumTools.CoordinateAsync(brief: "hi", cwd: cwd, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("role 'architect' runs on claude, opencode, cursor, copilot only", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Sources, exception.Message, StringComparison.Ordinal);
        Assert.Contains(".claustrum/roles/architect/role.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    [Fact]
    public void CastCreateRefusesADemoAuthorOnAnotherHarnessAndWritesNoFile()
    {
        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(
                () => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "claude:opus", ["demo-author"] = "opencode:some-model" }));

            Assert.Contains("'opencode:some-model' puts demo-author on 'opencode', but demo-author runs on claude only", exception.Message, StringComparison.Ordinal);
            Assert.Contains("or 'not needed'", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(CastStore.DirectoryFor(cwd)));
        });
    }

    [Fact]
    public void CastCreateRefusesAnArchitectSpawnedOnApiAndOffersHostInstead()
    {
        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(
                () => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["architect"] = "spawned on api:some-model" }));

            Assert.Contains("puts architect on 'api'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("or 'host'", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(CastStore.DirectoryFor(cwd)));
        });
    }

    [Fact]
    public void CastCreateNamesEveryProblemAtOnceAndOffersNoWayOutForTheBuilder()
    {
        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.CastCreate(new Dictionary<string, string>
            {
                ["architect"] = "spawned on api:a",
                ["builder"] = "api:b",
                ["demo-author"] = "cursor:c",
                ["ui-reviewer"] = "copilot:d",
            }));

            Assert.Contains("puts architect on 'api'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("puts builder on 'api'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("puts demo-author on 'cursor'", exception.Message, StringComparison.Ordinal);
            Assert.Contains("puts ui-reviewer on 'copilot'", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(CastStore.DirectoryFor(cwd)));
        });
    }

    [Fact]
    public void CastCreateSavesADemoAuthorThatIsNotNeeded()
    {
        InCwd(() =>
        {
            string json = ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "claude:opus", ["demo-author"] = "not needed", ["ui-reviewer"] = "claude:sonnet" });

            using JsonDocument created = JsonDocument.Parse(json);
            JsonElement roles = created.RootElement.GetProperty("roles");
            Assert.Equal(JsonValueKind.Null, roles.GetProperty("demo-author").ValueKind);
            Assert.Equal("claude:sonnet", roles.GetProperty("ui-reviewer").GetProperty("model").GetString());
            Assert.True(CastStore.Exists(cwd, CastStore.DefaultName));
        });
    }

    [Fact]
    public void CastCreateSavesTheBrowserRolesOnClaudeAndTheOthersOnAnyListedHarness()
    {
        InCwd(() =>
        {
            ClaustrumTools.CastCreate(new Dictionary<string, string>
            {
                ["architect"] = "spawned on opencode:some-model",
                ["builder"] = "cursor:b",
                ["tester"] = "copilot:t",
                ["code-reviewer"] = "api:r",
                ["demo-author"] = "claude:sonnet",
                ["ui-reviewer"] = "claude:sonnet",
            });

            Assert.True(CastStore.Exists(cwd, CastStore.DefaultName));
        });
    }

    // Config.Load now runs inside cast_create, so a claustrum.json that cannot be read is this call's own
    // error — surfaced with the file's name, nothing saved.
    [Fact]
    public void CastCreateOnAMalformedClaustrumJsonThrowsNamingTheFileAndSavesNothing()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), "{ not json");

        InCwd(() =>
        {
            McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "claude:opus" }));

            Assert.Contains("claustrum.json", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(CastStore.DirectoryFor(cwd)));
        });
    }

    private static void AssertRefusal(McpException exception, string role, string backend)
    {
        Assert.Contains($"role '{role}' runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{backend}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Sources, exception.Message, StringComparison.Ordinal);
        Assert.Contains($".claustrum/roles/{role}/role.json", exception.Message, StringComparison.Ordinal);
    }

    // cast_create writes into the server's own working directory (no cwd parameter), so the test moves
    // there and always back — ClaustrumToolsTests' own pattern for the cast tools.
    private void InCwd(Action body)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = cwd;
        try
        {
            body();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private string[] JobDirectories()
    {
        string root = JobDirectory.ResolveRoot(fixture.Platform);
        return Directory.Exists(root) ? [.. Directory.GetDirectories(root).Order(StringComparer.Ordinal)] : [];
    }
}
