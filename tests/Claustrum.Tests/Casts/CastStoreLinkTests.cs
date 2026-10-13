using Claustrum.Casts;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #103 at the store: a cast file or directory that is a link leaving the cwd is neither read, listed nor written, and the
// refusal names the file and where it really leads. The fence root is the cwd passed in, so the temp tree is a `.git`-less
// directory on purpose. A link that stays inside the cwd is followed as before.
public sealed class CastStoreLinkTests : IDisposable
{
    private const string Refusal = "resolves outside the working directory";

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-link-").FullName;
    private readonly CastLinkFixture links;

    public CastStoreLinkTests() => links = new CastLinkFixture(cwd);

    public void Dispose()
    {
        TempTree.Delete(cwd);
        links.Dispose();
    }

    private static Cast SampleCast(string name = "default") =>
        new(name, "1.0.0", new CastArchitect(CastArchitect.Host), [], BudgetUsd: null);

    public static TheoryData<string> Shapes() => [.. CastLinkFixture.Shapes];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void SaveThroughALinkLeavingTheCwdIsRefusedAndTouchesNothingOutside(string shape)
    {
        links.Arrange(shape);
        string[] before = links.Snapshot();

        CastException ex = Assert.Throws<CastException>(() => CastStore.Save(cwd, SampleCast()));

        Assert.Contains(Refusal, ex.Message, StringComparison.Ordinal);
        Assert.Contains(links.RealPathFor("default"), ex.Message, CastLinkFixture.PathComparison);
        Assert.Contains(CastStore.PathFor(cwd, "default"), ex.Message, CastLinkFixture.PathComparison);
        Assert.Equal(before, links.Snapshot());
        Assert.Equal(CastLinkFixture.Secret, File.ReadAllText(links.RealPathFor("default")));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void LoadThroughALinkLeavingTheCwdIsRefusedAndReadsNothing(string shape)
    {
        links.Arrange(shape);

        CastException ex = Assert.Throws<CastException>(() => CastStore.Load(cwd, "default"));

        Assert.Contains(Refusal, ex.Message, StringComparison.Ordinal);
        Assert.Contains(links.RealPathFor("default"), ex.Message, CastLinkFixture.PathComparison);
        Assert.DoesNotContain(CastLinkFixture.Secret.Trim(), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void TryLoadDefaultThroughALinkLeavingTheCwdIsRefused(string shape)
    {
        links.Arrange(shape);

        CastException ex = Assert.Throws<CastException>(() => CastStore.TryLoadDefault(cwd));

        Assert.Contains(Refusal, ex.Message, StringComparison.Ordinal);
    }

    // `leaf` links one file, so the directory listing is still the repo's own; only a linked directory leaks names.
    [Theory]
    [InlineData("claustrum")]
    [InlineData("casts")]
    public void ListNamesThroughALinkedDirectoryLeavingTheCwdIsRefused(string shape)
    {
        links.Arrange(shape);

        CastException ex = Assert.Throws<CastException>(() => CastStore.ListNames(cwd));

        Assert.Contains(Refusal, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeafLinkThatStaysInsideTheCwdIsReadAndWrittenThrough()
    {
        string realDirectory = Directory.CreateDirectory(Path.Combine(cwd, "real")).FullName;
        string real = Path.Combine(realDirectory, "kept.json");
        File.WriteAllText(real, CastLinkFixture.ValidCast);
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        TestLinks.Create(CastStore.PathFor(cwd, "default"), real);

        Cast loaded = CastStore.Load(cwd, "default");
        CastStore.Save(cwd, SampleCast() with { BudgetUsd = 7m });

        Assert.Equal("mine", loaded.Name);
        Assert.Contains("\"budget_usd\": 7", File.ReadAllText(real), StringComparison.Ordinal);
    }

    [Fact]
    public void ACastsDirectoryLinkThatStaysInsideTheCwdIsFollowed()
    {
        string realCasts = Directory.CreateDirectory(Path.Combine(cwd, "shared-casts")).FullName;
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum"));
        TestLinks.CreateDirectoryLink(CastStore.DirectoryFor(cwd), realCasts);

        CastStore.Save(cwd, SampleCast("team"));

        Assert.True(File.Exists(Path.Combine(realCasts, "team.json")));
        Assert.Equal(["team"], CastStore.ListNames(cwd));
        Assert.Equal("team", CastStore.Load(cwd, "team").Name);
    }
}
