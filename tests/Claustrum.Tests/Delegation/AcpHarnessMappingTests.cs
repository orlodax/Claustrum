using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Delegation;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #130: a role is judged and rendered by the harness of the backend it resolved to. `acp-opencode` is a backend of its own that plays opencode's roles:
// role.json `harnesses` lists `opencode` for it, the role renders for opencode, and a refusal names both. Everything here is a Prepare, which writes
// no job directory and starts no backend (HarnessRefusalTests is the native counterpart).
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class AcpHarnessMappingTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-acp-harness-").FullName;

    public void Dispose() => TempTree.Delete(cwd);

    private DelegateRequest Request(string role, ConfigOverrides overrides, ResumePin? pin = null) => new(
        Role: role,
        Brief: "hi",
        Cwd: cwd,
        Tier: "high",
        Overrides: overrides,
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        Pin: pin);

    private string[] JobDirectories()
    {
        string root = JobDirectory.ResolveRoot(fixture.Platform);
        return Directory.Exists(root) ? [.. Directory.GetDirectories(root).Order(StringComparer.Ordinal)] : [];
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("tester")]
    [InlineData("code-reviewer")]
    [InlineData("architect")]
    public void ARoleThatListsOpencodePreparesOnAcpOpencode(string role)
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "acp-opencode")));

        Assert.Equal("acp-opencode", prepared.Role.Backend);
    }

    [Fact]
    public void NoRoleJsonListsTheAcpBackendItselfItListsHarnesses()
    {
        RoleLibrary library = new();

        foreach (string role in library.ListRoles())
            Assert.DoesNotContain("acp-opencode", library.LoadRole(role, cwd).Definition.Harnesses);

        Assert.Contains("opencode", library.LoadRole("builder", cwd).Definition.Harnesses);
    }

    // The role is rendered for opencode: the system prompt is the one `--backend opencode` gets, not one rendered for a harness called acp-opencode.
    [Theory]
    [InlineData("builder")]
    [InlineData("tester")]
    [InlineData("code-reviewer")]
    public void TheRoleIsRenderedForOpencodeWhenItRunsOnAcpOpencode(string role)
    {
        PreparedDelegation native = DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "opencode", Model: "opencode:fake-model")));
        PreparedDelegation claude = DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "claude", Model: "claude:fake-model")));
        PreparedDelegation acp = DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "acp-opencode", Model: "acp-opencode:fake-model")));

        Assert.Equal(native.Role.SystemPrompt, acp.Role.SystemPrompt);
        Assert.NotEqual(claude.Role.SystemPrompt, acp.Role.SystemPrompt);
        Assert.Equal("acp-opencode", acp.Role.Backend);
        Assert.Equal(native.Role.Model, acp.Role.Model);
        Assert.Equal(native.Role.Permission.Level, acp.Role.Permission.Level);
    }

    [Fact]
    public void AnAliasSpellingPicksTheBackendAndKeepsTheModelIdWithItsVariant()
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Model: "acp-opencode:openrouter/deepseek/deepseek-v4-flash#max")));

        Assert.Equal("acp-opencode", prepared.Role.Backend);
        Assert.Equal("openrouter/deepseek/deepseek-v4-flash#max", prepared.Role.Model);
    }

    // Opt-in: neither a bare model id nor the native backend's spelling lands on it.
    [Fact]
    public void NothingButTheAcpSpellingResolvesToTheAcpBackend()
    {
        Assert.Equal("opencode", DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Model: "opencode:some-model"))).Role.Backend);
        Assert.Equal("claude", DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Model: "some-model"))).Role.Backend);
        Assert.Equal("claude", DelegateEngine.Prepare(Request("builder", new ConfigOverrides())).Role.Backend);
    }

    [Fact]
    public void ThePathOfBackendsAcpOpencodeInClaustrumJsonIsTheOneTheRunLaunches()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), /*lang=json,strict*/ """{"backends":{"acp-opencode":{"path":"/opt/fake/opencode"}}}""");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Backend: "acp-opencode")));

        Assert.Equal("/opt/fake/opencode", prepared.Options.BackendConfig?.Path);
    }

    [Fact]
    public void ThePathConfiguredForTheNativeBackendIsNotTheAcpBackendsOne()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), /*lang=json,strict*/ """{"backends":{"opencode":{"path":"/opt/native/opencode"}}}""");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Backend: "acp-opencode")));

        Assert.Null(prepared.Options.BackendConfig);
    }

    [Fact]
    public void ALiveRequestIsKeptForAcpOpencodeBecauseItHoldsAConversation()
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Backend: "acp-opencode")) with { Live = true, ReplyTimeoutSeconds = 60 });

        Assert.True(prepared.Options.Live);
        Assert.Equal(60, prepared.Options.ReplyTimeoutSeconds);
    }

    // ---- the refusal ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public void AClaudeOnlyRoleOnAcpOpencodeIsRefusedNamingBothTheBackendAndItsHarnessAndMintsNoJob(string role)
    {
        string[] before = JobDirectories();

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "acp-opencode"))));

        Assert.Contains($"role '{role}' runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("this run resolved it to 'acp-opencode' (harness 'opencode') through --backend/--model, the cast or claustrum.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("if this repo ships the role's parts for 'opencode', add it to `harnesses`", exception.Message, StringComparison.Ordinal);
        Assert.Contains($".claustrum/roles/{role}/role.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    [Fact]
    public async Task TheBlockingPathRefusesTheSameWayAndMintsNoJobAsync()
    {
        string[] before = JobDirectories();

        RoleRenderException exception = await Assert.ThrowsAsync<RoleRenderException>(
            () => DelegateEngine.RunAsync(Request("ui-reviewer", new ConfigOverrides(Model: "acp-opencode:some-model")), TestContext.Current.CancellationToken));

        Assert.Contains("'acp-opencode' (harness 'opencode')", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    // A native backend's refusal is worded as it always was: one name, no harness in parentheses.
    [Fact]
    public void ANativeBackendsRefusalStillNamesOnlyTheBackend()
    {
        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("ui-reviewer", new ConfigOverrides(Backend: "opencode"))));

        Assert.Contains("this run resolved it to 'opencode' through --backend/--model", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(harness", exception.Message, StringComparison.Ordinal);
    }

    // The way out the message names is the harness's: a repo that ships the role's parts for opencode lists opencode, and then acp-opencode runs it.
    [Fact]
    public void ALocalRoleJsonThatListsOpencodeWithItsPartsLetsTheRoleRunOnAcpOpencodeRenderedForOpencode()
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", "demo-author");
        Directory.CreateDirectory(Path.Combine(directory, "parts"));
        File.WriteAllText(Path.Combine(directory, "role.json"), /*lang=json,strict*/ """{"harnesses":["claude","opencode"]}""");
        File.WriteAllText(Path.Combine(directory, "parts", "browser.default.md"), "Drive the browser with this harness's own tools.");
        File.WriteAllText(Path.Combine(directory, "parts", "environment.default.md"), "Use the host's shell.");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("demo-author", new ConfigOverrides(Backend: "acp-opencode")));

        Assert.Equal("acp-opencode", prepared.Role.Backend);
        Assert.Contains("Drive the browser with this harness's own tools.", prepared.Role.SystemPrompt, StringComparison.Ordinal);
    }

    // ---- a continuation stays where its session began --------------------------------------------------------------------------

    [Fact]
    public void AContinuationOfAnAcpJobIsPinnedToAcpOpencodeAndItsModelWhateverTodaysConfigurationSays()
    {
        ResumePin pin = new("acp-opencode", "opencode/fake", Permission: null);

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(), pin) with { ParentJobId = "20261016-000000-aaaaaaaa", ResumeSession = "ses_old" });

        Assert.Equal("acp-opencode", prepared.Role.Backend);
        Assert.Equal("opencode/fake", prepared.Role.Model);
        Assert.Equal("ses_old", prepared.Request.ResumeSession);
        string warning = Assert.Single(prepared.Request.PreRunWarnings ?? [], text => text.Contains("ran as acp-opencode:opencode/fake", StringComparison.Ordinal));
        Assert.Contains("today's configuration would run it as", warning, StringComparison.Ordinal);
        Assert.Contains("the continuation stays on acp-opencode:opencode/fake", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AContinuationPinnedToAcpOpencodeSaysNothingWhenTodaysConfigurationIsTheSame()
    {
        ResumePin pin = new("acp-opencode", "opencode/fake", Permission: null);

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Model: "acp-opencode:opencode/fake"), pin) with { ParentJobId = "20261016-000000-aaaaaaaa", ResumeSession = "ses_old" });

        Assert.Equal("acp-opencode", prepared.Role.Backend);
        Assert.DoesNotContain(prepared.Request.PreRunWarnings ?? [], text => text.Contains("ran as", StringComparison.Ordinal));
    }
}
