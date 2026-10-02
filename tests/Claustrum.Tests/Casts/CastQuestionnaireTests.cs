using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

public sealed class CastQuestionnaireTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-questionnaire-").FullName;
    private readonly RoleLibrary roleLibrary = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static Config EmptyConfig() => new()
    {
        Merged = new ConfigDocument(
            Models: new Dictionary<string, string> { ["frontier-coding"] = "claude:opus", ["fast"] = "claude:haiku" },
            Roles: [],
            Backends: [],
            Defaults: null,
            Jobs: null),
        Origins = [],
    };

    [Fact]
    public async Task IncludesOneQuestionPerLibraryRolePlusArchitectAndBudgetAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        string[] keys = [.. result.Questions.Select(q => q.Key)];
        Assert.Contains("architect", keys);
        Assert.Contains("builder", keys);
        Assert.Contains("code-reviewer", keys);
        Assert.Contains("tester", keys);
        Assert.Contains("budget", keys);
    }

    [Fact]
    public async Task OnlyBuilderDisallowsNotNeededAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        Assert.False(result.Questions.Single(q => q.Key == "builder").AllowNotNeeded);
        Assert.True(result.Questions.Single(q => q.Key == "code-reviewer").AllowNotNeeded);
        Assert.True(result.Questions.Single(q => q.Key == "tester").AllowNotNeeded);
    }

    [Fact]
    public async Task ModelAliasesWhoseBackendIsNotFoundAreExcludedFromOptionsAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: false)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        Assert.Empty(result.Questions.Single(q => q.Key == "builder").Options);
    }

    [Fact]
    public async Task ModelAliasesWhoseBackendIsFoundAreOfferedAsOptionsAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        Assert.Contains("frontier-coding", result.Questions.Single(q => q.Key == "builder").Options);
        Assert.Contains("fast", result.Questions.Single(q => q.Key == "builder").Options);
    }

    // The library now ships a real "architect" role (roles/architect/role.json): the fixed
    // host/spawned question at key "architect" must be the ONLY question at that key — the per-role
    // loop skips the library role of the same name rather than asking about it twice (which would
    // trip AddQuestion's duplicate-key guard).
    [Fact]
    public async Task ArchitectRoleGetsExactlyOneFixedHostSpawnedQuestionAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        CastQuestion architect = Assert.Single(result.Questions, q => q.Key == "architect");
        Assert.Equal(CastArchitect.Host, architect.Options[0]);
        Assert.Contains($"{CastBuilder.SpawnedOn}frontier-coding", architect.Options);
        Assert.Contains($"{CastBuilder.SpawnedOn}fast", architect.Options);
        Assert.True(architect.AllowFreeForm);
    }

    [Fact]
    public async Task EveryEmittedQuestionKeyIsUniqueAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        string[] keys = [.. result.Questions.Select(q => q.Key)];
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // One alias per backend a role might land on: the demo-author and ui-reviewer run on claude alone
    // (#43), so offering them an opencode or api alias would offer an answer `run` refuses.
    private static Config AliasPerBackendConfig() => new()
    {
        Merged = new ConfigDocument(
            Models: new Dictionary<string, string>
            {
                ["claude-alias"] = "claude:opus",
                ["opencode-alias"] = "opencode:some-model",
                ["api-alias"] = "api:some-model",
            },
            Roles: [],
            Backends: [],
            Defaults: null,
            Jobs: null),
        Origins = [],
    };

    private static BackendRegistry EveryBackendFound() => new([new FakeBackend("claude", found: true), new FakeBackend("opencode", found: true), new FakeBackend("api", found: true)]);

    [Fact]
    public async Task AsksExactlyTheEightQuestionsInPipelineOrderDemoAuthorIncludedAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        Assert.Equal(
            ["architect", "builder", "code-reviewer", "demo-author", "tester", "ui-reviewer", "builder_max_parallel", "budget"],
            result.Questions.Select(question => question.Key));
    }

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    public async Task ABrowserRoleIsOfferedOnlyTheAliasesThatLandOnClaudeAsync(string role)
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        CastQuestion question = result.Questions.Single(q => q.Key == role);
        Assert.Equal(["claude-alias"], question.Options);
        Assert.True(question.AllowNotNeeded);
        Assert.True(question.AllowFreeForm);
    }

    [Theory]
    [InlineData("builder")]
    [InlineData("tester")]
    public async Task ARoleListedOnFourHarnessesGetsEveryAliasExceptApiAsync(string role)
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        Assert.Equal(["claude-alias", "opencode-alias"], result.Questions.Single(q => q.Key == role).Options);
    }

    [Fact]
    public async Task TheCodeReviewerListsApiSoItAlsoGetsTheApiAliasAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        Assert.Equal(["api-alias", "claude-alias", "opencode-alias"], result.Questions.Single(q => q.Key == "code-reviewer").Options);
    }

    // Both filters apply: an alias is offered only if its backend is installed AND the role runs there.
    [Fact]
    public async Task AnAliasOnABackendThatIsNotFoundIsDroppedBeforeTheHarnessFilterAsync()
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true), new FakeBackend("opencode", found: false), new FakeBackend("api", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, AliasPerBackendConfig(), cwd, CancellationToken.None);

        Assert.Equal(["claude-alias"], result.Questions.Single(q => q.Key == "builder").Options);
        Assert.Equal(["api-alias", "claude-alias"], result.Questions.Single(q => q.Key == "code-reviewer").Options);
    }

    // Free-form answers stay open (the check is `cast create`'s), so the prompt is where a person learns
    // which harnesses the role runs on.
    [Fact]
    public async Task EveryRolePromptSaysWhereTheRoleRunsAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        Assert.Contains("It runs on claude only.", result.Questions.Single(q => q.Key == "demo-author").Prompt, StringComparison.Ordinal);
        Assert.Contains("It runs on claude only.", result.Questions.Single(q => q.Key == "ui-reviewer").Prompt, StringComparison.Ordinal);
        Assert.Contains("It runs on claude, opencode, cursor, copilot only.", result.Questions.Single(q => q.Key == "builder").Prompt, StringComparison.Ordinal);
        Assert.Contains("It runs on claude, opencode, cursor, copilot, api only.", result.Questions.Single(q => q.Key == "code-reviewer").Prompt, StringComparison.Ordinal);
        foreach (CastQuestion question in result.Questions.Where(q => q.Key is not (Cast.ArchitectRole or CastQuestionnaire.MaxParallelKey or "budget")))
            Assert.Contains("It runs on ", question.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheArchitectIsNeverOfferedToBeSpawnedOnAnApiAliasAsync()
    {
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, EveryBackendFound(), AliasPerBackendConfig(), cwd, CancellationToken.None);

        CastQuestion architect = result.Questions.Single(q => q.Key == "architect");
        Assert.Equal([CastArchitect.Host, "spawned on claude-alias", "spawned on opencode-alias"], architect.Options);
        Assert.DoesNotContain(architect.Options, option => option.Contains("api-alias", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingCastsListsWhatIsAlreadyOnDiskAsync()
    {
        CastStore.Save(cwd, new Cast("staging", "1.0.0", new CastArchitect("host"), [], null));
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        Assert.Contains("staging", result.ExistingCasts);
    }
}
