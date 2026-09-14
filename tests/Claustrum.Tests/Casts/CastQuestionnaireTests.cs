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

    // Finding #9's duplicate-key guard cannot be provoked directly: RoleLibrary is sealed, reads
    // embedded assembly resources, and ListRoles ignores local overrides, so there is no seam to
    // inject a role named "architect" — and adding one to production code for a single throw was
    // judged not worth it (CastBuilder's equivalent guard is tested directly). These two pin the
    // guard's precondition instead, and fail on the day a library role would collide.
    [Fact]
    public void NoLibraryRoleCollidesWithAFixedQuestionKey()
    {
        Assert.DoesNotContain("architect", roleLibrary.ListRoles());
        Assert.DoesNotContain("budget", roleLibrary.ListRoles());
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
