using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SyncRootOsPlacementCollectionDefinition
{
    public const string Name = "SyncRoot OS placement counter";
}

// #105 on Windows with real links: a link whose target is spelled as a network location is refused on its spelling, in
// every spelling Windows resolves, and the walk never asks the OS to place it (SyncRoot.OsPlacements does not move; the
// counter is process-wide, so this class runs alone). The address is loopback and nothing is ever opened. A machine that
// will not make a symbolic link skips the case; elsewhere these are ordinary link names and the class skips.
[Collection(SyncRootOsPlacementCollectionDefinition.Name)]
public sealed class SyncRootNetworkLinkTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-netlink-").FullName;

    public void Dispose() => LinkFixtures.DeleteTree(root);

    public static TheoryData<string> Spellings() =>
    [
        @"\\?\UNC\127.0.0.1\c$\claustrum-never-opened",
        @"\\?\unc\127.0.0.1\c$\claustrum-never-opened",
        @"\\127.0.0.1\c$\claustrum-never-opened",
        @"\\.\UNC\127.0.0.1\c$\claustrum-never-opened",
        @"\\?\GLOBALROOT\Device\Mup\127.0.0.1\c$\claustrum-never-opened",
    ];

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("a network-spelled link target is refused on its spelling on Windows only.");
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void ADirectoryLinkToANetworkSpellingEscapesIsUnresolvedAndNeverReachesTheOs(string target)
    {
        RequireWindows();
        LinkFixtures.DirectoryLink(Path.Combine(root, "net"), target);
        string path = Path.Combine(root, "net");
        int before = SyncRoot.OsPlacements;

        bool escapes = SyncRoot.Repo(root).Escapes(path);
        string resolved = SyncRoot.Resolve(path, out bool unresolved);
        EscapeVerdict verdict = SyncRoot.Repo(root).ProbeEscaping(path);

        Assert.True(escapes);
        Assert.True(unresolved);
        Assert.Contains("127.0.0.1", resolved, StringComparison.Ordinal);
        Assert.Equal(EscapeVerdict.Present, verdict);
        Assert.Equal(before, SyncRoot.OsPlacements);
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void AFileLinkToANetworkSpellingEscapesAndIsPresentWithoutBeingOpened(string target)
    {
        RequireWindows();
        string path = Path.Combine(root, "agent.md");
        LinkFixtures.FileLink(path, target);
        int before = SyncRoot.OsPlacements;

        Assert.True(SyncRoot.Repo(root).Escapes(path));
        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(path));
        Assert.Equal(before, SyncRoot.OsPlacements);
    }

    [Fact]
    public void ANetworkLinkMidPathRefusesEverythingBelowIt()
    {
        RequireWindows();
        LinkFixtures.DirectoryLink(Path.Combine(root, "net"), @"\\?\UNC\127.0.0.1\c$\claustrum-never-opened");
        string path = Path.Combine(root, "net", "agents", "builder.md");
        int before = SyncRoot.OsPlacements;

        string resolved = SyncRoot.Resolve(path, out bool unresolved);

        Assert.True(SyncRoot.Repo(root).Escapes(path));
        Assert.True(unresolved);
        Assert.EndsWith(Path.Combine("agents", "builder.md"), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, SyncRoot.OsPlacements);
    }
}
