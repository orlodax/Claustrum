using System.Text.Json.Nodes;
using Claustrum.Casts;

namespace Claustrum.Tests.Casts;

// #121: `resolved` is what a cast's alias resolved to when the cast was created. Older casts (and hand-written ones) have none and
// must keep loading - a missing `resolved` means "nothing to compare", never an error and never a drift warning.
public sealed class CastResolvedStoreTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-resolved-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private void WriteRaw(string name, string json)
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        File.WriteAllText(CastStore.PathFor(cwd, name), json);
    }

    [Fact]
    public void ACastWrittenBeforeResolvedExistedLoadsWithNullResolvedEverywhere()
    {
        WriteRaw("old", /*lang=json,strict*/ """
            {"name":"old","library":"1.0.0","architect":{"mode":"spawned","model":"frontier-reasoning","tier":null},
             "roles":{"builder":{"model":"cheap-coding","backend":null,"tier":null,"max_parallel":2},"tester":null},"budget_usd":4}
            """);

        Cast cast = CastStore.Load(cwd, "old");

        Assert.Null(cast.Architect.Resolved);
        Assert.Equal("cheap-coding", cast.Roles["builder"]!.Model);
        Assert.Null(cast.Roles["builder"]!.Resolved);
        Assert.Equal(2, cast.Roles["builder"]!.MaxParallel);
    }

    [Fact]
    public void AMinimalArchitectOfJustAModeStillLoads()
    {
        WriteRaw("host", /*lang=json,strict*/ """{"name":"host","library":"1.0.0","architect":{"mode":"host"},"roles":{},"budget_usd":null}""");

        Assert.Null(CastStore.Load(cwd, "host").Architect.Resolved);
    }

    [Fact]
    public void ResolvedRoundTripsAndIsWrittenAsSnakeCaseNextToEveryEntry()
    {
        Cast cast = new(
            "default", "1.0.0", new CastArchitect(CastArchitect.Spawned, "frontier-reasoning", Tier: null, Resolved: "claude:opus"),
            new Dictionary<string, CastRoleEntry?> { ["builder"] = new CastRoleEntry("cheap-coding", null, null, Resolved: "claude:haiku") },
            BudgetUsd: null);

        CastStore.Save(cwd, cast);
        Cast loaded = CastStore.Load(cwd, "default");

        Assert.Equal("claude:opus", loaded.Architect.Resolved);
        Assert.Equal("claude:haiku", loaded.Roles["builder"]!.Resolved);
        JsonNode written = JsonNode.Parse(File.ReadAllText(CastStore.PathFor(cwd, "default")))!;
        Assert.Equal("claude:haiku", written["roles"]!["builder"]!["resolved"]!.GetValue<string>());
        Assert.Equal("claude:opus", written["architect"]!["resolved"]!.GetValue<string>());
    }
}
