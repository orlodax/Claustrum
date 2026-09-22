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

    [Fact]
    public async Task ExistingCastsListsWhatIsAlreadyOnDiskAsync()
    {
        CastStore.Save(cwd, new Cast("staging", "1.0.0", new CastArchitect("host"), [], null));
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);

        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, EmptyConfig(), cwd, CancellationToken.None);

        Assert.Contains("staging", result.ExistingCasts);
    }
}
