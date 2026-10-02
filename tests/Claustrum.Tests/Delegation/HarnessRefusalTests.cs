using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// DelegateEngine.RequireSupportedHarness (#43, PR #42): a role resolved to a harness its role.json does
// not list is refused in Prepare — before Render, before any job directory — because the demo-author's and
// ui-reviewer's browser part is claude-only and a role on a harness nobody wrote it for runs without the
// tools it needs. Only a backend Claustrum registers is judged; an unknown name keeps its old failure.
// None of these starts a backend: every refusal throws, and the one run below targets "nonexistent".
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class HarnessRefusalTests(AppServicesHomeFixture fixture) : IDisposable
{
    private const string Sources = "--backend/--model, the cast or claustrum.json";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-harness-refusal-").FullName;

    public void Dispose() => TempTree.Delete(cwd);

    public static TheoryData<string, string> BrowserRolesOnOtherHarnesses()
    {
        TheoryData<string, string> data = [];
        foreach (string role in new[] { "demo-author", "ui-reviewer" })
        {
            foreach (string backend in new[] { "opencode", "cursor", "copilot", "api" })
                data.Add(role, backend);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BrowserRolesOnOtherHarnesses))]
    public void ABrowserRoleOnAnotherHarnessIsRefusedBeforeAnyJobDirectoryExists(string role, string backend)
    {
        string[] before = JobDirectories();

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: backend))));

        Assert.Contains($"role '{role}' runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{backend}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Sources, exception.Message, StringComparison.Ordinal);
        Assert.Contains($".claustrum/roles/{role}/role.json", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    // RunAsync(DelegateRequest) is what `run` and MCP `delegate` call: it must refuse the same way, and
    // before Runner could mint a directory of its own.
    [Theory]
    [MemberData(nameof(BrowserRolesOnOtherHarnesses))]
    public async Task TheBlockingPathRefusesTheSameWayAndMintsNoJobAsync(string role, string backend)
    {
        string[] before = JobDirectories();

        RoleRenderException exception = await Assert.ThrowsAsync<RoleRenderException>(
            () => DelegateEngine.RunAsync(Request(role, new ConfigOverrides(Backend: backend)), TestContext.Current.CancellationToken));

        Assert.Contains("runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    [Fact]
    public void ABuilderOnApiIsRefusedAndTheMessageListsItsFourHarnesses()
    {
        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("builder", new ConfigOverrides(Backend: "api"))));

        Assert.Contains("role 'builder' runs on claude, opencode, cursor, copilot only", exception.Message, StringComparison.Ordinal);
        Assert.Contains(".claustrum/roles/builder/role.json", exception.Message, StringComparison.Ordinal);
    }

    // The architect's harnesses exclude api too, and `coordinate` reaches it through the cast's model.
    [Fact]
    public void AnArchitectOnApiIsRefused()
    {
        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("architect", new ConfigOverrides(Model: "api:gpt-x"))));

        Assert.Contains("role 'architect' runs on claude, opencode, cursor, copilot only", exception.Message, StringComparison.Ordinal);
        Assert.Contains(".claustrum/roles/architect/role.json", exception.Message, StringComparison.Ordinal);
    }

    // Controls: the refusal is the role's own list, not a blanket "no api" or "claude only" — a role that
    // lists the harness still prepares there, and a browser role still prepares on claude.
    [Fact]
    public void ARoleThatListsApiStillPreparesOnApi()
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request("code-reviewer", new ConfigOverrides(Backend: "api")));

        Assert.Equal("api", prepared.Role.Backend);
    }

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public void ABrowserRoleStillPreparesOnClaude(string role)
    {
        PreparedDelegation prepared = DelegateEngine.Prepare(Request(role, new ConfigOverrides(Backend: "claude")));

        Assert.Equal("claude", prepared.Role.Backend);
    }

    // The message says "--backend/--model, the cast or claustrum.json" because cast entries arrive as
    // overrides and the engine cannot tell them from a flag: each source must reach the same check.
    [Fact]
    public void AModelFlagThatLandsOnAnotherHarnessIsRefused()
    {
        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("demo-author", new ConfigOverrides(Model: "opencode:some-model"))));

        Assert.Contains("runs on claude only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACastEntryThatLandsOnAnotherHarnessIsRefused()
    {
        CastStore.Save(cwd, new Cast(
            "default", "1.0.0", new CastArchitect(CastArchitect.Host),
            new Dictionary<string, CastRoleEntry?> { ["demo-author"] = new CastRoleEntry(Model: "cursor:some-model", Backend: null, Tier: null) },
            BudgetUsd: null));
        (string tier, ConfigOverrides overrides, _, _, _) = CastApplication.Resolve(cwd, "demo-author", castName: null, tierFlag: null, new ConfigOverrides());

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("demo-author", overrides) with { Tier = tier }));

        Assert.Contains("runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'cursor'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClaustrumJsonRoleModelThatLandsOnAnotherHarnessIsRefused()
    {
        // Config.Load reads claustrum.json from the git root, and GitRootLocator wants only a `.git` entry.
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), /*lang=json,strict*/ """{"roles":{"ui-reviewer":{"model":"copilot:some-model"}}}""");

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("ui-reviewer", new ConfigOverrides())));

        Assert.Contains("role 'ui-reviewer' runs on claude only", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'copilot'", exception.Message, StringComparison.Ordinal);
    }

    // A backend name Claustrum does not register is not judged here: `builder` on it reaches Runner's
    // registry check as before — backend_missing — and the system prompt is already on disk by then.
    [Fact]
    public async Task ABuilderOnAnUnregisteredBackendStillEndsBackendMissingWithItsSystemPromptWrittenAsync()
    {
        RunResult result = await DelegateEngine.RunAsync(Request("builder", new ConfigOverrides(Backend: "nonexistent")), TestContext.Current.CancellationToken);

        Assert.Equal(RunStatus.BackendMissing, result.Status);
        string systemMd = Path.Combine(JobDirectory.ResolveRoot(fixture.Platform), result.JobId, "system.md");
        Assert.True(File.Exists(systemMd), $"{systemMd} was not written");
    }

    // For the same name the demo-author fails earlier, at Render, because its browser part exists for
    // claude alone — and the message names that part, not the harness list.
    [Fact]
    public void ADemoAuthorOnAnUnregisteredBackendFailsAtRenderNamingTheBrowserPart()
    {
        string[] before = JobDirectories();

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("demo-author", new ConfigOverrides(Backend: "nonexistent"))));

        Assert.Contains("part 'browser'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("runs on", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, JobDirectories());
    }

    // The way out the message names: a repo that ships the parts for another harness adds it to
    // `harnesses` in its own role.json. Both halves are needed — with the harness alone the refusal is
    // gone and Render's own error about the missing part takes its place.
    [Fact]
    public void ALocalRoleJsonThatAddsAHarnessLiftsTheRefusalAndLeavesTheMissingPartToRender()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"harnesses":["claude","opencode"]}""");

        RoleRenderException exception = Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("demo-author", new ConfigOverrides(Backend: "opencode"))));

        Assert.DoesNotContain("runs on", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no part 'browser' for harness 'opencode'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalRoleJsonWithTheHarnessAndItsPartsPreparesOnThatHarness()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"harnesses":["claude","opencode"]}""");
        WriteLocalPart("demo-author", "browser.default.md", "Drive the browser with this harness's own tools.");
        WriteLocalPart("demo-author", "environment.default.md", "Use the host's shell.");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request("demo-author", new ConfigOverrides(Backend: "opencode")));

        Assert.Equal("opencode", prepared.Role.Backend);
        Assert.Contains("Drive the browser with this harness's own tools.", prepared.Role.SystemPrompt, StringComparison.Ordinal);
    }

    // The override is per role: lifting it for the demo-author leaves the ui-reviewer refused.
    [Fact]
    public void ALocalOverrideForOneRoleDoesNotLiftTheRefusalForTheOther()
    {
        WriteLocalRoleJson("demo-author", /*lang=json,strict*/ """{"harnesses":["claude","opencode"]}""");

        Assert.Throws<RoleRenderException>(() => DelegateEngine.Prepare(Request("ui-reviewer", new ConfigOverrides(Backend: "opencode"))));
    }

    private DelegateRequest Request(string role, ConfigOverrides overrides) => new(
        Role: role,
        Brief: "hi",
        Cwd: cwd,
        Tier: "high",
        Overrides: overrides,
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024);

    // Diffed rather than asserted empty: the jobs root is the fixture-wide home every class in this
    // collection shares (AppServicesHomeFixture), so an earlier test's own job may already be there.
    private string[] JobDirectories()
    {
        string root = JobDirectory.ResolveRoot(fixture.Platform);
        return Directory.Exists(root) ? [.. Directory.GetDirectories(root).Order(StringComparer.Ordinal)] : [];
    }

    private void WriteLocalRoleJson(string role, string json)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "role.json"), json);
    }

    private void WriteLocalPart(string role, string fileName, string text)
    {
        string directory = Path.Combine(cwd, ".claustrum", "roles", role, "parts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), text);
    }
}
