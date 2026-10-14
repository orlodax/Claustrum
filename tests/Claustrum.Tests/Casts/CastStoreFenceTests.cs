using Claustrum.Casts;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Casts;

// #103/#105 at the store, the cases CastStoreLinkTests leaves: a link whose escaping location provably holds nothing is
// "no such cast" (no refusal, nothing created), a leaf link to a nonexistent outside target is refused as something
// that is there, and a pipe among the casts is a CastException instead of a hang (2026-10-13, found by review).
public sealed class CastStoreFenceTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cast-fence-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-cast-fence-out-").FullName;

    public void Dispose()
    {
        TempTree.Delete(cwd);
        TempTree.Delete(outside);
    }

    // An empty outside directory makes the link's real location provably absent.
    [Fact]
    public void AClaustrumLinkWhoseOutsideDirectoryHoldsNoCastsIsNoSuchCastNotARefusal()
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cwd, ".claustrum"), outside);

        Assert.Null(CastStore.TryLoadDefault(cwd));
        Assert.Empty(CastStore.ListNames(cwd));
        Assert.False(CastStore.Exists(cwd, "default"));
        CastException ex = Assert.Throws<CastException>(() => CastStore.Load(cwd, "default"));
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // `.claustrum/casts` linked to an existing outside directory is a directory that is there: listing it would leak names.
    [Fact]
    public void ACastsLinkToAnExistingOutsideDirectoryIsRefusedEvenWhenItIsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum"));
        TestLinks.CreateDirectoryLink(CastStore.DirectoryFor(cwd), outside);

        Assert.Throws<CastException>(() => CastStore.ListNames(cwd));
        Assert.Null(CastStore.TryLoadDefault(cwd));
    }

    [Fact]
    public void ASaveThroughALinkedDirectoryWhoseOutsideLocationHoldsNothingIsStillRefused()
    {
        TestLinks.CreateDirectoryLink(Path.Combine(cwd, ".claustrum"), outside);

        CastException ex = Assert.Throws<CastException>(() => CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Host), [], BudgetUsd: null)));

        Assert.Contains("resolves outside the working directory", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    // The target does not exist, but the link does: "absent" would let a later write create the target outside.
    [Fact]
    public void ALeafLinkToANonexistentOutsideTargetIsRefusedNotTreatedAsMissing()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        string target = Path.Combine(outside, "not-there.json");
        TestLinks.Create(CastStore.PathFor(cwd, "default"), target);

        Assert.Throws<CastException>(() => CastStore.Exists(cwd, "default"));
        CastException load = Assert.Throws<CastException>(() => CastStore.Load(cwd, "default"));
        Assert.Throws<CastException>(() => CastStore.TryLoadDefault(cwd));
        Assert.Throws<CastException>(() => CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Host), [], BudgetUsd: null)));

        Assert.Contains("resolves outside the working directory", load.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [Fact]
    public void ALeafLinkLoopIsRefused()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        string a = Path.Combine(CastStore.DirectoryFor(cwd), "a.json");
        string b = Path.Combine(CastStore.DirectoryFor(cwd), "default.json");
        TestLinks.Create(a, b);
        TestLinks.Create(b, a);

        Assert.Throws<CastException>(() => CastStore.Load(cwd, "default"));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("mine")]
    public void AFifoCastIsRefusedAsNotARegularFileWithoutHanging(string name)
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        PipeFiles.MakeFifo(CastStore.PathFor(cwd, name));

        CastException ex = PipeFiles.Within(() => Assert.Throws<CastException>(() => CastStore.Load(cwd, name)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
        Assert.Contains(CastStore.PathFor(cwd, name), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingOntoAFifoIsACastExceptionNotAHang()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        PipeFiles.MakeFifo(CastStore.PathFor(cwd, "default"));

        CastException ex = PipeFiles.Within(() => Assert.Throws<CastException>(() =>
            CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Host), [], BudgetUsd: null))));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFifoDefaultCastIsRefusedByTryLoadDefaultWithoutHanging()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        PipeFiles.MakeFifo(CastStore.PathFor(cwd, "default"));

        CastException ex = PipeFiles.Within(() => Assert.Throws<CastException>(() => CastStore.TryLoadDefault(cwd)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFifoAmongTheCastsStillListsByName()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        PipeFiles.MakeFifo(CastStore.PathFor(cwd, "pipe"));
        File.WriteAllText(CastStore.PathFor(cwd, "real"), CastLinkFixture.ValidCast);

        string[] names = PipeFiles.Within(() => CastStore.ListNames(cwd));

        Assert.Equal(["pipe", "real"], names);
    }

    // #67: a blank name or library is as missing as an absent one.
    [Theory]
    [InlineData(/*lang=json,strict*/ """{"name":"","library":"","architect":{"mode":"host"},"roles":{},"budget_usd":null}""")]
    [InlineData(/*lang=json,strict*/ """{"name":"   ","library":"  \t","architect":{"mode":"host"},"roles":{},"budget_usd":null}""")]
    [InlineData(/*lang=json,strict*/ """{"name":null,"library":null,"architect":{"mode":"host"},"roles":{},"budget_usd":null}""")]
    public void ABlankNameOrLibraryCountsAsMissing(string json)
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        File.WriteAllText(CastStore.PathFor(cwd, "blank"), json);

        Cast loaded = CastStore.Load(cwd, "blank");

        Assert.Equal("blank", loaded.Name);
        Assert.Equal(AppServices.RoleLibrary.Version, loaded.Library);
    }

    [Fact]
    public void ANameWithoutALibraryKeepsTheNameAndTakesTheShippedLibrary()
    {
        Directory.CreateDirectory(CastStore.DirectoryFor(cwd));
        File.WriteAllText(CastStore.PathFor(cwd, "half"), /*lang=json,strict*/ """{"name":"mine","architect":{"mode":"host"},"roles":{},"budget_usd":null}""");

        Cast loaded = CastStore.Load(cwd, "half");

        Assert.Equal("mine", loaded.Name);
        Assert.Equal(AppServices.RoleLibrary.Version, loaded.Library);
    }
}
