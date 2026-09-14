using Claustrum.Casts;

namespace Claustrum.Tests.Casts;

public sealed class CastStoreTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static Cast SampleCast(string name = "default") => new(
        Name: name,
        Library: "1.0.0",
        Architect: new CastArchitect("host"),
        Roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: "opencode:openrouter/deepseek/deepseek-v4-pro", Backend: null, Tier: null),
            ["code-reviewer"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: "xhigh"),
            ["tester"] = null,
        },
        BudgetUsd: 10m);

    [Fact]
    public void SaveThenLoadRoundTripsEveryField()
    {
        Cast cast = SampleCast();

        CastStore.Save(cwd, cast);
        Cast loaded = CastStore.Load(cwd, "default");

        // Not Assert.Equal(cast, loaded): Cast's record-generated Equals falls back to
        // Dictionary<>'s reference equality for Roles, so two structurally-identical casts loaded
        // from separate calls would never compare equal — assert field by field instead.
        Assert.Equal(cast.Name, loaded.Name);
        Assert.Equal(cast.Library, loaded.Library);
        Assert.Equal(cast.Architect, loaded.Architect);
        Assert.Equal(cast.BudgetUsd, loaded.BudgetUsd);
        Assert.Equal(cast.Roles.Keys.OrderBy(k => k, StringComparer.Ordinal), loaded.Roles.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string role, CastRoleEntry? entry) in cast.Roles)
            Assert.Equal(entry, loaded.Roles[role]);
    }

    [Fact]
    public void NullRoleEntrySurvivesTheRoundTripAsNotNeeded()
    {
        CastStore.Save(cwd, SampleCast());

        Cast loaded = CastStore.Load(cwd, "default");

        Assert.True(loaded.Roles.ContainsKey("tester"));
        Assert.Null(loaded.Roles["tester"]);
    }

    [Fact]
    public void NullBudgetRoundTripsAsUnlimitedNotAsMissing()
    {
        Cast unlimited = SampleCast() with { BudgetUsd = null };

        CastStore.Save(cwd, unlimited);
        Cast loaded = CastStore.Load(cwd, "default");

        Assert.Null(loaded.BudgetUsd);
    }

    [Fact]
    public void LoadMissingCastThrowsCastException()
    {
        Assert.Throws<CastException>(() => CastStore.Load(cwd, "nope"));
    }

    [Fact]
    public void LoadMalformedJsonThrowsCastExceptionNamingTheFile()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        string path = CastStore.PathFor(cwd, "broken");
        File.WriteAllText(path, "{ not json");

        CastException ex = Assert.Throws<CastException>(() => CastStore.Load(cwd, "broken"));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoadDefaultReturnsNullWhenNoDefaultCastExists()
    {
        Assert.Null(CastStore.TryLoadDefault(cwd));
    }

    [Fact]
    public void TryLoadDefaultReturnsTheCastWhenItExists()
    {
        CastStore.Save(cwd, SampleCast());

        Assert.NotNull(CastStore.TryLoadDefault(cwd));
    }

    [Fact]
    public void ListNamesReturnsEveryCastSortedOrdinally()
    {
        CastStore.Save(cwd, SampleCast("zeta"));
        CastStore.Save(cwd, SampleCast("alpha"));

        Assert.Equal(["alpha", "zeta"], CastStore.ListNames(cwd));
    }

    [Fact]
    public void ListNamesReturnsEmptyWhenNoCastsDirectoryExists()
    {
        Assert.Empty(CastStore.ListNames(cwd));
    }
}
