using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #130: `acp-opencode` plays the roles of its harness, so a cast judges it as `opencode` (role.json `harnesses` lists harnesses, never `acp-opencode`).
// CastHarnessCheck is checked against the real registry, whose AcpBackend says its own harness; the questionnaire against a found stand-in that says the same.
public sealed class CastAcpHarnessTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-acp-").FullName;
    private readonly RoleLibrary library = new();
    private readonly BackendRegistry registry = BackendRegistry.CreateDefault(new FakePlatform());

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static Config ConfigWith(params (string Alias, string Spec)[] aliases) => new()
    {
        Merged = new ConfigDocument(
            Models: aliases.ToDictionary(entry => entry.Alias, entry => entry.Spec, StringComparer.Ordinal),
            Roles: [],
            Backends: [],
            Defaults: null,
            Jobs: null),
        Origins = [],
    };

    private string? Problem(string role, string? spec, Config? config = null, bool fallback = false) =>
        CastHarnessCheck.Problem(role, spec, library, registry, config ?? ConfigWith(), cwd, fallback);

    // ---- CastHarnessCheck ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("builder")]
    [InlineData("tester")]
    [InlineData("code-reviewer")]
    [InlineData(Cast.ArchitectRole)]
    public void ARoleThatListsOpencodeMayRunOnAnAcpOpencodeAlias(string role)
    {
        Assert.Null(Problem(role, "acp-opencode:opencode/fake"));
    }

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public void AClaudeOnlyRoleIsRefusedOnAcpOpencodeNamingTheBackendTheSpecAndTheHarnessesItRunsOn(string role)
    {
        string? problem = Problem(role, "acp-opencode:opencode/fake");

        Assert.NotNull(problem);
        Assert.Contains($"'acp-opencode:opencode/fake' puts {role} on 'acp-opencode'", problem, StringComparison.Ordinal);
        Assert.Contains($"{role} runs on claude only", problem, StringComparison.Ordinal);
        Assert.Contains("(its role.json `harnesses`)", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasThatChasesToAnAcpSpecIsJudgedByItsHarnessToo()
    {
        Config config = ConfigWith(("cheap", "acp-opencode:opencode/fake"), ("cheaper", "cheap"));

        Assert.Null(Problem("builder", "cheaper", config));
        string? problem = Problem("demo-author", "cheaper", config);
        Assert.NotNull(problem);
        Assert.Contains("'cheaper' puts demo-author on 'acp-opencode'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFallbackOnAcpOpencodeFollowsTheSameRule()
    {
        Assert.Null(Problem("builder", "acp-opencode:opencode/fake", fallback: true));

        string? problem = Problem("ui-reviewer", "acp-opencode:opencode/fake", fallback: true);

        Assert.NotNull(problem);
        Assert.Contains("puts ui-reviewer's fallback on 'acp-opencode'", problem, StringComparison.Ordinal);
        Assert.Contains("'none'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ACastWithAnAcpBuilderAndAnAcpSpawnedArchitectIsAcceptedAndOneWithAnAcpDemoAuthorIsNot()
    {
        Cast accepted = new(
            "ok", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "acp-opencode:opencode/fake"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry(Model: "acp-opencode:opencode/fake", Backend: null, Tier: null) },
            BudgetUsd: null);
        Cast refused = new(
            "bad", "1.0.0", new CastArchitect(CastArchitect.Host),
            new Dictionary<string, CastRoleEntry?> { ["demo-author"] = new CastRoleEntry(Model: "acp-opencode:opencode/fake", Backend: null, Tier: null) },
            BudgetUsd: null);

        CastHarnessCheck.Require(accepted, library, registry, ConfigWith(), cwd);
        CastException exception = Assert.Throws<CastException>(() => CastHarnessCheck.Require(refused, library, registry, ConfigWith(), cwd));

        Assert.Contains("'acp-opencode:opencode/fake' puts demo-author on 'acp-opencode'", exception.Message, StringComparison.Ordinal);
    }

    // Only the exact name reaches the ACP backend: a spec that merely starts alike is an unregistered backend, which the cast leaves to the run.
    [Fact]
    public void ANameThatOnlyLooksLikeTheAcpBackendIsNotJudgedHere()
    {
        Assert.Null(Problem("demo-author", "acp-opencodex:some-model"));
        Assert.Null(Problem("demo-author", "acp:some-model"));
    }

    // ---- CastQuestionnaire ---------------------------------------------------------------------------------------------

    private static Config AliasPerBackend() => ConfigWith(
        ("claude-alias", "claude:opus"),
        ("opencode-alias", "opencode:some-model"),
        ("acp-alias", "acp-opencode:some-model"));

    private static BackendRegistry EveryBackendFound() => new(
    [
        new FakeBackend("claude", found: true),
        new FakeBackend("opencode", found: true),
        new FakeBackend("acp-opencode", found: true, harness: "opencode"),
    ]);

    [Theory]
    [InlineData("builder")]
    [InlineData("tester")]
    [InlineData("code-reviewer")]
    public async Task ARoleThatListsOpencodeIsOfferedTheAcpAliasAlongsideTheOthersAsync(string role)
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, EveryBackendFound(), AliasPerBackend(), cwd, TestContext.Current.CancellationToken);

        Assert.Equal(["acp-alias", "claude-alias", "opencode-alias"], result.Questions.Single(q => q.Key == role).Options);
    }

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public async Task AClaudeOnlyRoleIsNeverOfferedTheAcpAliasAsync(string role)
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, EveryBackendFound(), AliasPerBackend(), cwd, TestContext.Current.CancellationToken);

        Assert.Equal(["claude-alias"], result.Questions.Single(q => q.Key == role).Options);
        Assert.Equal(["none", "claude-alias"], result.Questions.Single(q => q.Key == $"{role}_fallback").Options);
    }

    [Fact]
    public async Task TheArchitectMayBeSpawnedOnTheAcpAliasAndItsFallbackIsOfferedItTooAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, EveryBackendFound(), AliasPerBackend(), cwd, TestContext.Current.CancellationToken);

        Assert.Equal([CastArchitect.Host, "spawned on acp-alias", "spawned on claude-alias", "spawned on opencode-alias"], result.Questions.Single(q => q.Key == Cast.ArchitectRole).Options);
        Assert.Equal(["none", "acp-alias", "claude-alias", "opencode-alias"], result.Questions.Single(q => q.Key == "builder_fallback").Options);
    }

    [Fact]
    public async Task AnAcpAliasIsDroppedWhenTheAcpBackendIsNotFoundLikeAnyOtherAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true), new FakeBackend("opencode", found: true), new FakeBackend("acp-opencode", found: false, harness: "opencode")]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, backends, AliasPerBackend(), cwd, TestContext.Current.CancellationToken);

        Assert.Equal(["claude-alias", "opencode-alias"], result.Questions.Single(q => q.Key == "builder").Options);
    }

    // The prompt says where the role runs, by role.json: it names harnesses, never the ACP backend.
    [Fact]
    public async Task ThePromptsStillNameHarnessesNotTheAcpBackendAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, EveryBackendFound(), AliasPerBackend(), cwd, TestContext.Current.CancellationToken);

        Assert.All(result.Questions, question => Assert.DoesNotContain("acp-opencode", question.Prompt, StringComparison.Ordinal));
        Assert.Contains("It runs on claude, opencode, cursor, copilot only.", result.Questions.Single(q => q.Key == "builder").Prompt, StringComparison.Ordinal);
    }

    // A backend the registry does not know stands for itself, as before: an alias on a stand-in name is offered only to a role that lists that name.
    [Fact]
    public async Task AnAliasOnAnUnregisteredBackendIsStillJudgedByItsOwnNameAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);
        Config config = ConfigWith(("claude-alias", "claude:opus"), ("ghost-alias", "ghost:model"));

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(library, backends, config, cwd, TestContext.Current.CancellationToken);

        Assert.Equal(["claude-alias"], result.Questions.Single(q => q.Key == "builder").Options);
    }
}
