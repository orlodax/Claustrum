using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #105: ProbeEscaping is Escapes for a caller that would stat the path. Inside: stat as usual. Absent: it leaves the root
// and nothing is at its real path. Present: something is there, or the walk cannot say, so refuse. Nothing is ever created.
public sealed class SyncRootProbeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-probe-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-probe-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(root);
        LinkFixtures.DeleteTree(outside);
    }

    private string[] OutsideSnapshot() => LinkFixtures.Snapshot(outside);

    [Fact]
    public void APathInsideTheRootIsInsideWhetherOrNotItExists()
    {
        SyncRoot fence = SyncRoot.Repo(root);
        File.WriteAllText(Path.Combine(root, "there.md"), "x");

        Assert.Equal(EscapeVerdict.Inside, fence.ProbeEscaping(Path.Combine(root, "there.md")));
        Assert.Equal(EscapeVerdict.Inside, fence.ProbeEscaping(Path.Combine(root, "not", "there.md")));
    }

    [Fact]
    public void AnUnfencedRootIsInsideForAnythingEvenAnOutsidePath()
    {
        SyncRoot global = SyncRoot.Global(root);

        Assert.Equal(EscapeVerdict.Inside, global.ProbeEscaping(Path.Combine(outside, "anything.md")));
    }

    [Fact]
    public void AMidPathDirectoryLinkToAnOutsideDirectoryLackingTheLeafIsAbsent()
    {
        LinkFixtures.DirectoryLink(Path.Combine(root, "linked"), outside);
        string[] before = OutsideSnapshot();

        EscapeVerdict verdict = SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "linked", "agents", "builder.md"));

        Assert.Equal(EscapeVerdict.Absent, verdict);
        Assert.Equal(before, OutsideSnapshot());
    }

    [Fact]
    public void AMidPathDirectoryLinkWhoseOutsideLeafIsAFileIsPresent()
    {
        LinkFixtures.DirectoryLink(Path.Combine(root, "linked"), outside);
        File.WriteAllText(Path.Combine(outside, "builder.md"), "secret");

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "linked", "builder.md")));
    }

    [Fact]
    public void AMidPathDirectoryLinkWhoseOutsideLeafIsADirectoryIsPresent()
    {
        LinkFixtures.DirectoryLink(Path.Combine(root, "linked"), outside);
        Directory.CreateDirectory(Path.Combine(outside, "agents"));

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "linked", "agents")));
    }

    [Fact]
    public void ALeafLinkToANonexistentOutsideTargetIsPresentBecauseTheLinkItselfIsThere()
    {
        string target = Path.Combine(outside, "not-yet.md");
        LinkFixtures.FileLink(Path.Combine(root, "leaf.md"), target);

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "leaf.md")));
        Assert.False(File.Exists(target));
        Assert.Empty(OutsideSnapshot());
    }

    [Fact]
    public void ALeafLinkToAnExistingOutsideFileIsPresent()
    {
        File.WriteAllText(Path.Combine(outside, "real.md"), "x");
        LinkFixtures.FileLink(Path.Combine(root, "leaf.md"), Path.Combine(outside, "real.md"));

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "leaf.md")));
    }

    [Fact]
    public void ALinkLoopIsPresent()
    {
        LinkFixtures.DirectoryLink(Path.Combine(root, "a"), Path.Combine(root, "b"));
        LinkFixtures.DirectoryLink(Path.Combine(root, "b"), Path.Combine(root, "a"));

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "a", "x.md")));
    }

    [Fact]
    public void ALinkThatStaysInsideIsInside()
    {
        Directory.CreateDirectory(Path.Combine(root, "real"));
        LinkFixtures.DirectoryLink(Path.Combine(root, "linked"), Path.Combine(root, "real"));

        Assert.Equal(EscapeVerdict.Inside, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "linked", "x.md")));
    }

    // An NT-namespace target the string walk cannot place is placed by the OS; one that leaves the root is Present
    // whether or not the leaf exists, because nothing may be statted on an answer that is not a plain local path.
    [Fact]
    public void AnOpaqueEscapingLinkIsPresentOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("an NT-namespace link target is a Windows form.");

        LinkFixtures.DirectoryLink(Path.Combine(root, "nt"), @"\\?\GLOBALROOT\??\" + outside);

        Assert.Equal(EscapeVerdict.Present, SyncRoot.Repo(root).ProbeEscaping(Path.Combine(root, "nt", "missing.md")));
        Assert.Empty(OutsideSnapshot());
    }
}
