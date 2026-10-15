using System.Text.Json;
using Claustrum.Casts;
using Claustrum.Mcp;
using Claustrum.Tests.Testing;
using ModelContextProtocol;

namespace Claustrum.Tests.Mcp;

// #122 at the MCP door, in-process: cast_questions, cast_create, delegate, delegate_async and coordinate refuse a machine with neither a
// user config nor a repo claustrum.json - the same message the CLI prints, as a tool error, with no job minted - while the tools that
// need no configuration (list_roles, cast_list) still answer. Run on a platform whose home is empty, restored afterwards (AppServicesScope).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class UninitializedMachineToolsTests : IDisposable
{
    private readonly string home = Directory.CreateTempSubdirectory("claustrum-mcp-uninit-home-").FullName;
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-mcp-uninit-cwd-").FullName;
    private readonly string previousCwd = Environment.CurrentDirectory;
    private readonly AppServicesScope scope;

    public UninitializedMachineToolsTests()
    {
        scope = new AppServicesScope(new HomeRedirectPlatform(home));
        Environment.CurrentDirectory = cwd;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = previousCwd;
        scope.Dispose();
        TempTree.Delete(cwd);
        TempTree.Delete(home);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JobsDirectory => Path.Combine(home, ".claustrum", "jobs");

    private void AssertRefusal(McpException exception) =>
        Assert.Equal($"this repo isn't initialized: no user config at {UserConfig.PathFor(home)} and no claustrum.json at <no git root>; run `claustrum init`", exception.Message);

    [Fact]
    public async Task CastQuestionsIsRefusedAsync() =>
        AssertRefusal(await Assert.ThrowsAsync<McpException>(() => ClaustrumTools.CastQuestionsAsync(Ct)));

    [Fact]
    public void CastCreateIsRefusedAndSavesNoCast()
    {
        McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "claude:opus" }));

        AssertRefusal(exception);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum")));
    }

    [Fact]
    public async Task DelegateIsRefusedAndMintsNoJobAsync()
    {
        McpException exception = await Assert.ThrowsAsync<McpException>(() => ClaustrumTools.DelegateAsync("builder", "do it", cwd: cwd, cancellationToken: Ct));

        AssertRefusal(exception);
        Assert.False(Directory.Exists(JobsDirectory));
    }

    [Fact]
    public void DelegateAsyncIsRefusedOnTheCallItselfAndStartsNoJob()
    {
        McpException exception = Assert.Throws<McpException>(() => ClaustrumTools.DelegateStart("builder", "do it", cwd: cwd));

        AssertRefusal(exception);
        Assert.False(Directory.Exists(JobsDirectory));
    }

    [Fact]
    public async Task CoordinateIsRefusedAndMintsNoJobAsync()
    {
        McpException exception = await Assert.ThrowsAsync<McpException>(() => ClaustrumTools.CoordinateAsync(brief: "x", cwd: cwd, cancellationToken: Ct));

        AssertRefusal(exception);
        Assert.False(Directory.Exists(JobsDirectory));
    }

    [Fact]
    public void TheToolsThatNeedNoConfigurationStillAnswer()
    {
        using JsonDocument roles = JsonDocument.Parse(ClaustrumTools.ListRoles());
        using JsonDocument casts = JsonDocument.Parse(ClaustrumTools.CastList());

        Assert.NotEmpty(roles.RootElement.EnumerateArray());
        Assert.Empty(casts.RootElement.EnumerateArray());
    }

    // The same tools on an initialized machine: cast_create stores what each alias resolved to (#121).
    [Fact]
    public void CastCreateOnAnInitializedMachineStoresTheResolvedTargetOfEveryModel()
    {
        UserConfig.Write(home, /*lang=json,strict*/ """{"models":{"fast":"claude:sonnet"}}""");

        string json = ClaustrumTools.CastCreate(new Dictionary<string, string> { ["builder"] = "fast", ["code-reviewer"] = "claude:opus", ["tester"] = "cheap-coding" });

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement roles = document.RootElement.GetProperty("roles");
        Assert.Equal("claude:sonnet", roles.GetProperty("builder").GetProperty("resolved").GetString());
        Assert.Equal("claude:opus", roles.GetProperty("code-reviewer").GetProperty("resolved").GetString());
        Assert.Equal("claude:haiku", roles.GetProperty("tester").GetProperty("resolved").GetString());
        Assert.Equal("claude:sonnet", CastStore.Load(cwd, "default").Roles["builder"]!.Resolved);
    }
}
