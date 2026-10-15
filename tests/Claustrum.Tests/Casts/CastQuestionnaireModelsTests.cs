using System.Text.Json.Nodes;
using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #121: `cast questions` shows every alias of the merged config with its target, the layer that defined it and where that
// layer lives, before it asks anything - so a silent built-in fallback is visible up front.
public sealed class CastQuestionnaireModelsTests
{
    private readonly RoleLibrary roleLibrary = new();

    private async Task<CastModelOption[]> ModelsAsync(TempConfig temp)
    {
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, temp.Config, temp.Cwd, TestContext.Current.CancellationToken);
        return result.Models ?? [];
    }

    [Fact]
    public async Task WithNoConfigEveryAliasIsBuiltInAndSortedByNameAsync()
    {
        using TempConfig temp = new();

        CastModelOption[] models = await ModelsAsync(temp);

        Assert.Equal(
        [
            new CastModelOption("cheap-coding", "claude:haiku", "builtin", "built-in default"),
            new CastModelOption("fast", "claude:haiku", "builtin", "built-in default"),
            new CastModelOption("frontier-coding", "claude:opus", "builtin", "built-in default"),
            new CastModelOption("frontier-reasoning", "claude:opus", "builtin", "built-in default"),
            new CastModelOption("standard-coding", "claude:sonnet", "builtin", "built-in default"),
        ],
            models);
    }

    [Fact]
    public async Task EachRowNamesTheLayerThatDefinedItAndTheFileThatLayerLivesInAsync()
    {
        using TempConfig temp = new(
            userJson: /*lang=json,strict*/ """{"models":{"standard-coding":"opencode:vendor/user-model","only-in-user":"claude:opus"}}""",
            repoJson: /*lang=json,strict*/ """{"models":{"fast":"claude:from-repo","standard-coding":"opencode:vendor/repo-wins"}}""");

        CastModelOption[] models = await ModelsAsync(temp);

        Assert.Equal(new CastModelOption("cheap-coding", "claude:haiku", "builtin", "built-in default"), models.Single(m => m.Alias == "cheap-coding"));
        Assert.Equal(new CastModelOption("only-in-user", "claude:opus", "user", temp.UserPath), models.Single(m => m.Alias == "only-in-user"));
        Assert.Equal(new CastModelOption("fast", "claude:from-repo", "repo", temp.RepoPath), models.Single(m => m.Alias == "fast"));
        // The repo layer is read after the user layer, so for an alias both define the repo's row is the one shown.
        Assert.Equal(new CastModelOption("standard-coding", "opencode:vendor/repo-wins", "repo", temp.RepoPath), models.Single(m => m.Alias == "standard-coding"));
        Assert.Equal(models.OrderBy(m => m.Alias, StringComparer.Ordinal), models);
    }

    // A user alias chained to a built-in is still the USER's row (its first hop), resolving through the built-in.
    [Fact]
    public async Task AUserAliasChainedToABuiltInIsAUserRowWhoseTargetIsTheBuiltInsAsync()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"mine":"cheap-coding"}}""");

        CastModelOption row = (await ModelsAsync(temp)).Single(m => m.Alias == "mine");

        Assert.Equal(new CastModelOption("mine", "claude:haiku", "user", temp.UserPath), row);
    }

    [Fact]
    public async Task TheModelsArrayIsSerializedAsAliasTargetLayerSourceInTheQuestionnaireJsonAsync()
    {
        using TempConfig temp = new(userJson: /*lang=json,strict*/ """{"models":{"fast":"claude:sonnet"}}""");
        BackendRegistry backends = new([new FakeBackend("claude", found: true)]);
        CastQuestionnaireResult result = await CastQuestionnaire.BuildAsync(roleLibrary, backends, temp.Config, temp.Cwd, TestContext.Current.CancellationToken);

        JsonNode json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result, CastJsonContext.Default.CastQuestionnaireResult))!;

        JsonArray models = json["models"]!.AsArray();
        JsonNode fast = models.Single(m => m!["alias"]!.GetValue<string>() == "fast")!;
        Assert.Equal("claude:sonnet", fast["target"]!.GetValue<string>());
        Assert.Equal("user", fast["layer"]!.GetValue<string>());
        Assert.Equal(temp.UserPath, fast["source"]!.GetValue<string>());
        Assert.Equal(["alias", "target", "layer", "source"], [.. fast.AsObject().Select(property => property.Key)]);
    }
}
