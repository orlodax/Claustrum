using Claustrum.Core.Config;
using Claustrum.Roles.Sync;
using CoreConfig = Claustrum.Core.Config.Config;

namespace Claustrum.Roles.Tests.Sync;

// #53, #35, #85: what `claustrum.json` and role.json change in the files each harness's sync writes. The models and efforts
// follow the config; a deny list is rendered where the harness has per-command deny syntax (opencode) and said not to be
// rendered elsewhere; `permission` is rendered nowhere; opencode's `variant:` is opt-in on a `#` in the model spec and its
// `mode: all` marks a primary role.
public sealed class SyncConfigFidelityTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-fidelity-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-fidelity-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(fakeHome);
    }

    private SyncResult Sync(string harness, CoreConfig? config, params string[] roles)
    {
        RoleRenderer renderer = new(library);
        return harness switch
        {
            "claude" => new ClaudeSync(library, renderer, fakeHome).Sync(cwd, roles, config: config),
            "opencode" => new OpencodeSync(library, renderer, fakeHome).Sync(cwd, roles, config: config),
            "copilot" => new CopilotSync(library, renderer, fakeHome).Sync(cwd, roles, config: config),
            "cursor" => new CursorSync(library, renderer, fakeHome).Sync(cwd, roles, config: config),
            _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "not a harness with a sync"),
        };
    }

    private string Agent(string harness, string name) => File.ReadAllText(HarnessSyncs.AgentFile(harness, HarnessSyncs.RepoRoot(harness, cwd), name));

    private static string Field(string file, string key)
    {
        string prefix = $"{key}: ";
        string? line = file.Split('\n').Skip(1).TakeWhile(l => l != "---").FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line[prefix.Length..];
    }

    private static bool HasField(string file, string key) =>
        file.Split('\n').Skip(1).TakeWhile(l => l != "---").Any(l => l.StartsWith($"{key}:", StringComparison.Ordinal));

    private static string Frontmatter(string file) => string.Join('\n', file.Split('\n').Skip(1).TakeWhile(l => l != "---"));

    private void WriteLocalRoleJson(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    // ---- models and efforts follow the config ------------------------------------------------------------------------

    [Fact]
    public void ClaudeModelAndEffortFollowTheConfigAndEffortMovesOnlyTheBaseAgent()
    {
        CoreConfig config = SyncConfigs.Make(
            models: new() { ["frontier-coding"] = "claude:opus-9" },
            roles: new() { ["builder"] = new RoleSettings(null, "low", null, null) });

        Sync("claude", config, "builder");

        Assert.Equal("opus-9", Field(Agent("claude", "builder"), "model"));
        Assert.Equal("low", Field(Agent("claude", "builder"), "effort"));
        Assert.Equal("opus-9", Field(Agent("claude", "builder-xhigh"), "model"));
        Assert.Equal("xhigh", Field(Agent("claude", "builder-xhigh"), "effort"));
        Assert.Equal("max", Field(Agent("claude", "builder-max"), "effort"));
    }

    [Fact]
    public void ClaudeWithNoConfigWritesTheBuiltInModelNames()
    {
        Sync("claude", null, "builder", "code-reviewer");

        Assert.Equal("opus", Field(Agent("claude", "builder"), "model"));
        Assert.Equal("sonnet", Field(Agent("claude", "code-reviewer"), "model"));
    }

    [Fact]
    public void CopilotTakesACopilotAlias()
    {
        Sync("copilot", SyncConfigs.Make(models: new() { ["frontier-coding"] = "copilot:gpt-x" }), "builder");

        Assert.Equal("gpt-x", Field(Agent("copilot", "builder"), "model"));
        Assert.Equal("gpt-x", Field(Agent("copilot", "builder-max"), "model"));
    }

    [Fact]
    public void CursorTakesACursorAlias()
    {
        Sync("cursor", SyncConfigs.Make(models: new() { ["frontier-coding"] = "cursor:composer-x" }), "builder");

        Assert.Equal("composer-x", Field(Agent("cursor", "builder"), "model"));
    }

    [Theory]
    [InlineData("copilot", "auto")]
    [InlineData("cursor", "inherit")]
    public void CopilotAndCursorKeepTheirDefaultsWhenTheAliasLandsElsewhere(string harness, string expected)
    {
        Sync(harness, SyncConfigs.Make(models: new() { ["frontier-coding"] = "claude:opus" }), "builder");

        Assert.Equal(expected, Field(Agent(harness, "builder"), "model"));
    }

    // ---- deny ----------------------------------------------------------------------------------------------------------

    private static CoreConfig DenyConfig(params string[] deny) => SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings(null, null, null, deny) });

    [Fact]
    public void ConfigDenyIsRenderedAsOpencodeBashPermissionAfterRoleJsonsOwn()
    {
        Sync("opencode", DenyConfig("git commit", "git push"), "builder");

        string front = Frontmatter(Agent("opencode", "builder"));
        string[] keys = [.. front.Split('\n').Where(line => line.EndsWith(": deny", StringComparison.Ordinal)).Select(line => line.Trim())];
        Assert.Equal(["\"git push\": deny", "\"git push *\": deny", "\"git commit\": deny", "\"git commit *\": deny"], keys);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void OtherHarnessesRecordThatConfigDenyIsNotRenderedAndWriteNoPermissionBlock(string harness)
    {
        SyncResult result = Sync(harness, DenyConfig("git commit"), "builder");

        Assert.Contains(result.Decisions ?? [], line => line.StartsWith($"{harness} builder: claustrum.json deny (git commit) not rendered", StringComparison.Ordinal));
        Assert.DoesNotContain("git commit", Agent(harness, "builder"), StringComparison.Ordinal);
    }

    [Fact]
    public void NoConfigDenyMeansNoNotRenderedLine()
    {
        foreach (string harness in new[] { "claude", "copilot", "cursor" })
            Assert.DoesNotContain(Sync(harness, SyncConfigs.Make(), "builder").Decisions ?? [], line => line.Contains("not rendered", StringComparison.Ordinal));
    }

    // ---- opencode permission.bash ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("builder")]
    [InlineData("builder-xhigh")]
    [InlineData("builder-max")]
    public void TheBuilderAndItsStubsDenyGitPushWithNoConfig(string agent)
    {
        Sync("opencode", null, "builder");

        string front = Frontmatter(Agent("opencode", agent));
        Assert.Contains("permission:\n  bash:\n    \"git push\": deny\n    \"git push *\": deny", front, StringComparison.Ordinal);
        Assert.DoesNotContain("\"*\": allow", front, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeReviewerHasNoPermissionBlock()
    {
        Sync("opencode", null, "code-reviewer");

        Assert.False(HasField(Agent("opencode", "code-reviewer"), "permission"));
        Assert.False(HasField(Agent("opencode", "code-reviewer-max"), "permission"));
    }

    [Fact]
    public void ADenyPatternRepeatedInConfigAndRoleJsonIsWrittenOnce()
    {
        Sync("opencode", DenyConfig("git push"), "builder");

        Assert.Equal(1, Frontmatter(Agent("opencode", "builder")).Split("\"git push\": deny").Length - 1);
    }

    [Fact]
    public void NoShippedOpencodeAgentEverAllowsEveryBashCommand()
    {
        Sync("opencode", DenyConfig("git commit"), [.. library.ListRoles().Where(role => library.LoadRole(role, cwd).Definition.Harnesses.Contains("opencode"))]);

        foreach (string file in Directory.EnumerateFiles(Path.Combine(cwd, ".opencode", "agent")))
            Assert.DoesNotContain("\"*\": allow", File.ReadAllText(file), StringComparison.Ordinal);
    }

    // ---- permission is rendered by no harness ---------------------------------------------------------------------------

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AConfigPermissionIsSaidNotRenderedOncePerHarnessAndChangesNoFile(string harness)
    {
        CoreConfig config = SyncConfigs.Make(roles: new() { ["builder"] = new RoleSettings(null, null, "readonly", null) });

        SyncResult withConfig = Sync(harness, config, "builder");
        string with = Agent(harness, "builder");
        Directory.Delete(Path.Combine(cwd, harness == "copilot" ? ".github" : $".{harness}"), recursive: true);
        Sync(harness, null, "builder");

        string[] lines = [.. (withConfig.Decisions ?? []).Where(line => line.Contains("claustrum.json permission (readonly) not rendered", StringComparison.Ordinal))];
        Assert.Single(lines);
        Assert.StartsWith($"{harness} builder:", lines[0], StringComparison.Ordinal);
        Assert.Equal(Agent(harness, "builder"), with);
    }

    // ---- #35: opencode variant is opt-in ------------------------------------------------------------------------------

    [Fact]
    public void VariantAppearsOnlyWhenTheModelSpecCarriesAHash()
    {
        Sync("opencode", SyncConfigs.Make(models: new() { ["frontier-coding"] = "opencode:openrouter/vendor/model#high" }), "builder");

        string front = Frontmatter(Agent("opencode", "builder"));
        Assert.Equal("openrouter/vendor/model", Field(Agent("opencode", "builder"), "model"));
        Assert.Equal("high", Field(Agent("opencode", "builder"), "variant"));
        Assert.Contains("variant: high", front, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("builder-xhigh")]
    [InlineData("builder-max")]
    public void TheDefaultAliasWritesNoVariantInTheBaseOrAnyStub(string agent)
    {
        Sync("opencode", null, "builder");

        Assert.False(HasField(Agent("opencode", agent), "variant"));
        Assert.Equal("openrouter/deepseek/deepseek-v4-pro", Field(Agent("opencode", agent), "model"));
    }

    [Fact]
    public void AModelWithNoHashWritesNoVariantEvenFromAConfigAlias()
    {
        Sync("opencode", SyncConfigs.Make(models: new() { ["frontier-coding"] = "opencode:openrouter/vendor/plain" }), "builder");

        Assert.False(HasField(Agent("opencode", "builder-max"), "variant"));
        Assert.Equal("openrouter/vendor/plain", Field(Agent("opencode", "builder-max"), "model"));
    }

    // ---- #85: a primary role is mode: all on opencode -------------------------------------------------------------------

    [Theory]
    [InlineData("architect")]
    [InlineData("architect-xhigh")]
    [InlineData("architect-max")]
    public void TheArchitectAndItsStubsAreModeAll(string agent)
    {
        Sync("opencode", null, "architect");

        Assert.Equal("all", Field(Agent("opencode", agent), "mode"));
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("builder-max")]
    [InlineData("tester")]
    [InlineData("code-reviewer")]
    public void EveryOtherRoleIsModeSubagent(string agent)
    {
        Sync("opencode", null, "builder", "tester", "code-reviewer");

        Assert.Equal("subagent", Field(Agent("opencode", agent), "mode"));
    }

    [Fact]
    public void ALocalPrimaryTrueFlipsARoleAndItsStubs()
    {
        WriteLocalRoleJson("builder", /*lang=json,strict*/ """{"primary":true}""");

        Sync("opencode", null, "builder");

        Assert.Equal("all", Field(Agent("opencode", "builder"), "mode"));
        Assert.Equal("all", Field(Agent("opencode", "builder-xhigh"), "mode"));
    }

    [Fact]
    public void ALocalPrimaryFalseTakesTheArchitectBackToSubagent()
    {
        WriteLocalRoleJson("architect", /*lang=json,strict*/ """{"primary":false}""");

        Sync("opencode", null, "architect");

        Assert.Equal("subagent", Field(Agent("opencode", "architect"), "mode"));
    }
}
