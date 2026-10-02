using Claustrum.Casts;
using Claustrum.Core.Backends;
using Claustrum.Core.Config;
using Claustrum.Roles;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// CastHarnessCheck (#43): the cast-creation half of DelegateEngine.RequireSupportedHarness. A spec is
// refused only when its backend is one Claustrum registers and the role's role.json does not list it, so
// a typo'd or third-party backend name is left to the run, and `Require` reports every offending entry
// at once instead of one per attempt.
public sealed class CastHarnessCheckTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-harness-").FullName;
    private readonly RoleLibrary library = new();
    private readonly BackendRegistry backends = FoundBackends("claude", "api", "opencode", "copilot", "cursor");

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static BackendRegistry FoundBackends(params string[] names) => new(names.Select(name => new FakeBackend(name, found: true)));

    private static Config ConfigWithAliases(params (string Alias, string Spec)[] aliases) => new()
    {
        Merged = new ConfigDocument(
            Models: aliases.ToDictionary(entry => entry.Alias, entry => entry.Spec, StringComparer.Ordinal),
            Roles: [],
            Backends: [],
            Defaults: null,
            Jobs: null),
        Origins = [],
    };

    private string? Problem(string role, string? spec, Config? config = null) =>
        CastHarnessCheck.Problem(role, spec, library, backends, config ?? ConfigWithAliases(), cwd);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSpecHasNoProblem(string? spec) => Assert.Null(Problem("demo-author", spec));

    // "nonexistent" is not a registered backend: the run keeps its own failure for it (backend_missing,
    // or a render error for a claude-only part), the cast does not second-guess it.
    [Fact]
    public void AnUnregisteredBackendHasNoProblemEvenForAClaudeOnlyRole() =>
        Assert.Null(Problem("demo-author", "nonexistent:some-model"));

    [Theory]
    [InlineData("demo-author", "claude:sonnet")]
    [InlineData("ui-reviewer", "claude:opus")]
    [InlineData("builder", "opencode:some-model")]
    [InlineData("tester", "claude:sonnet")]
    [InlineData("code-reviewer", "api:some-model")]
    [InlineData(Cast.ArchitectRole, "claude:opus")]
    public void ASpecOnAHarnessTheRoleListsHasNoProblem(string role, string spec) => Assert.Null(Problem(role, spec));

    // A bare id is claude's by the grammar (`SplitBackendModel`), so it is as supported as `claude:`.
    [Fact]
    public void ABareModelIdIsClaudeAndSupported() => Assert.Null(Problem("demo-author", "sonnet"));

    [Theory]
    [InlineData("demo-author", "opencode:some-model", "opencode", "claude")]
    [InlineData("demo-author", "cursor:some-model", "cursor", "claude")]
    [InlineData("ui-reviewer", "api:some-model", "api", "claude")]
    [InlineData("ui-reviewer", "copilot:some-model", "copilot", "claude")]
    [InlineData("tester", "api:some-model", "api", "claude, opencode, cursor, copilot")]
    [InlineData("builder", "api:some-model", "api", "claude, opencode, cursor, copilot")]
    public void AnUnsupportedHarnessNamesTheRoleTheBackendAndTheHarnessesItRunsOn(string role, string spec, string backend, string harnesses)
    {
        string? problem = Problem(role, spec);

        Assert.NotNull(problem);
        Assert.Contains($"'{spec}' puts {role} on '{backend}'", problem, StringComparison.Ordinal);
        Assert.Contains($"{role} runs on {harnesses} only", problem, StringComparison.Ordinal);
        Assert.Contains("(its role.json `harnesses`)", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("demo-author")]
    [InlineData("ui-reviewer")]
    [InlineData("tester")]
    public void ANormalRoleIsToldItMayBeNotNeeded(string role)
    {
        string? problem = Problem(role, "api:some-model");

        Assert.NotNull(problem);
        Assert.EndsWith("answer an alias that lands on one of those, or 'not needed'", problem, StringComparison.Ordinal);
    }

    // The builder is the one role a cast cannot do without, so its message offers no way out.
    [Fact]
    public void TheBuilderIsOfferedNoNotNeeded()
    {
        string? problem = Problem("builder", "api:some-model");

        Assert.NotNull(problem);
        Assert.Contains("builder runs on claude, opencode, cursor, copilot only", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("not needed", problem, StringComparison.Ordinal);
        Assert.EndsWith("answer an alias that lands on one of those", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchitectIsOfferedHost()
    {
        string? problem = Problem(Cast.ArchitectRole, "api:some-model");

        Assert.NotNull(problem);
        Assert.Contains("architect runs on claude, opencode, cursor, copilot only", problem, StringComparison.Ordinal);
        Assert.EndsWith("or 'host'", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("not needed", problem, StringComparison.Ordinal);
    }

    // The alias is chased with this machine's own claustrum.json: the problem quotes the spec as typed
    // (the alias) and names the backend it landed on.
    [Fact]
    public void AnAliasIsChasedToItsBackendAndQuotedAsTyped()
    {
        Config config = ConfigWithAliases(("shiny", "opencode:some-model"), ("shinier", "shiny"));

        string? problem = Problem("demo-author", "shinier", config);

        Assert.NotNull(problem);
        Assert.Contains("'shinier' puts demo-author on 'opencode'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasThatLandsOnAClaudeBackendHasNoProblem()
    {
        Config config = ConfigWithAliases(("cheap", "claude:haiku"));

        Assert.Null(Problem("ui-reviewer", "cheap", config));
    }

    [Fact]
    public void RequireAcceptsACastWhereEveryEntryIsSupportedNotNeededOrWithoutAModel()
    {
        Cast cast = new(
            "ok", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "opencode:a"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = Entry("claude:opus"),
                ["demo-author"] = null,
                ["ui-reviewer"] = Entry("claude:sonnet"),
                ["tester"] = new CastRoleEntry(Model: null, Backend: null, Tier: "high"),
            },
            BudgetUsd: null);

        CastHarnessCheck.Require(cast, library, backends, ConfigWithAliases(), cwd);
    }

    // `spawned` with no model leaves the architect on its own tier's model, which the cast cannot
    // judge: only a model the cast names is checked.
    [Fact]
    public void RequireAcceptsASpawnedArchitectThatNamesNoModel()
    {
        Cast cast = new("spawned", "1.0.0", new CastArchitect(CastArchitect.Spawned), [], null);

        CastHarnessCheck.Require(cast, library, backends, ConfigWithAliases(), cwd);
    }

    [Fact]
    public void RequireAcceptsAHostArchitect()
    {
        Cast cast = new("host", "1.0.0", new CastArchitect(CastArchitect.Host), [], null);

        CastHarnessCheck.Require(cast, library, backends, ConfigWithAliases(), cwd);
    }

    // Every problem in one throw, the architect's first, so a user fixes the whole cast in one pass; the
    // message opens with the cast's name and "not saved".
    [Fact]
    public void RequireAggregatesEveryProblemIntoOneCastException()
    {
        Cast cast = new(
            "broken", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "api:a"),
            new Dictionary<string, CastRoleEntry?>
            {
                ["builder"] = Entry("api:b"),
                ["demo-author"] = Entry("opencode:c"),
                ["ui-reviewer"] = Entry("api:d"),
                ["tester"] = Entry("claude:sonnet"),
                ["code-reviewer"] = null,
            },
            BudgetUsd: null);

        CastException exception = Assert.Throws<CastException>(() => CastHarnessCheck.Require(cast, library, backends, ConfigWithAliases(), cwd));

        Assert.StartsWith("cast 'broken' not saved: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'api:a' puts architect on 'api'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'api:b' puts builder on 'api'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'opencode:c' puts demo-author on 'opencode'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'api:d' puts ui-reviewer on 'api'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tester", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-reviewer", exception.Message, StringComparison.Ordinal);
        Assert.True(
            exception.Message.IndexOf("puts architect", StringComparison.Ordinal) < exception.Message.IndexOf("puts builder", StringComparison.Ordinal),
            "the architect's problem comes first");
        Assert.Equal(3, CountOf(exception.Message, "; "));
    }

    [Fact]
    public void RequireNamesASingleProblemWithoutASeparator()
    {
        Cast cast = new(
            "one", "1.0.0", new CastArchitect(CastArchitect.Host),
            new Dictionary<string, CastRoleEntry?> { ["demo-author"] = Entry("api:x") },
            BudgetUsd: null);

        CastException exception = Assert.Throws<CastException>(() => CastHarnessCheck.Require(cast, library, backends, ConfigWithAliases(), cwd));

        Assert.Equal(0, CountOf(exception.Message, "; "));
        Assert.Contains("puts demo-author on 'api'", exception.Message, StringComparison.Ordinal);
    }

    private static CastRoleEntry Entry(string model) => new(Model: model, Backend: null, Tier: null);

    private static int CountOf(string text, string phrase)
    {
        int count = 0;
        for (int index = text.IndexOf(phrase, StringComparison.Ordinal); index >= 0; index = text.IndexOf(phrase, index + phrase.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
