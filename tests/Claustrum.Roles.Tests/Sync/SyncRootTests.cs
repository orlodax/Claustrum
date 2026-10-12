using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #95: SyncRoot.Resolve follows every link on the way (directory and leaf alike) and leaves a tail that does not exist as
// typed; Escapes is the fence. `..` after a link is physical on Unix, as the kernel applies it, and lexical on Windows.
public sealed class SyncRootTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-syncroot-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Under(params string[] parts) => Path.Combine([root, .. parts]);

    private string RealDirectory(params string[] parts) => Directory.CreateDirectory(Under(parts)).FullName;

    [Fact]
    public void APathWithNoLinksComesBackUnchangedAndAMissingTailStaysAsTyped()
    {
        string existing = RealDirectory("real", "sub");
        string missing = Path.Combine(existing, "not", "there.md");

        Assert.Equal(existing, SyncRoot.Resolve(existing), LinkFixtures.PathComparer);
        Assert.Equal(missing, SyncRoot.Resolve(missing), LinkFixtures.PathComparer);
    }

    [Fact]
    public void ADirectoryLinkMidPathIsResolvedWithAnAbsoluteTarget()
    {
        RealDirectory("real", "sub");
        LinkFixtures.DirectoryLink(Under("link"), Under("real"));

        string resolved = SyncRoot.Resolve(Under("link", "sub", "missing", "file.md"));

        Assert.Equal(Under("real", "sub", "missing", "file.md"), resolved, LinkFixtures.PathComparer);
    }

    [Fact]
    public void ADirectoryLinkWithARelativeTargetIsResolvedFromTheLinksOwnDirectory()
    {
        RealDirectory("holder", "real", "sub");
        RealDirectory("holder", "links");
        LinkFixtures.DirectoryLink(Under("holder", "links", "rel"), Path.Combine("..", "real"));

        string resolved = SyncRoot.Resolve(Under("holder", "links", "rel", "sub", "file.md"));

        Assert.Equal(Under("holder", "real", "sub", "file.md"), resolved, LinkFixtures.PathComparer);
    }

    [Fact]
    public void AChainOfTwoLinksIsFollowedToTheEnd()
    {
        RealDirectory("real");
        LinkFixtures.DirectoryLink(Under("second"), Under("real"));
        LinkFixtures.DirectoryLink(Under("first"), "second");

        string resolved = SyncRoot.Resolve(Under("first", "file.md"));

        Assert.Equal(Under("real", "file.md"), resolved, LinkFixtures.PathComparer);
    }

    [Fact]
    public void ALeafFileLinkIsFollowedToo()
    {
        RealDirectory("real");
        File.WriteAllText(Under("real", "target.json"), "{}");
        LinkFixtures.FileLink(Under("leaf.json"), Under("real", "target.json"));

        Assert.Equal(Under("real", "target.json"), SyncRoot.Resolve(Under("leaf.json")), LinkFixtures.PathComparer);
    }

    // POSIX applies `..` after the link before it is followed: `x -> d/..` with `d -> outside/agents` is `outside`,
    // where a lexical collapse reads it as the directory holding `x` (the bypass found in review, 2026-10-12).
    [Fact]
    public void DotDotAfterALinkIsPhysicalOnUnixAndTheBypassEscapes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("`..` after a link is lexical on Windows; the physical reading is Unix's.");
            return;
        }

        string repo = RealDirectory("repo");
        string outsideAgents = RealDirectory("outside", "agents");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "d"), outsideAgents);
        LinkFixtures.DirectoryLink(Path.Combine(repo, "x"), "d/..");

        string resolved = SyncRoot.Resolve(Path.Combine(repo, "x", "file"));

        Assert.Equal(Under("outside", "file"), resolved);
        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "x", "file")));
        Assert.False(SyncRoot.Global(repo).Escapes(Path.Combine(repo, "x", "file")));
    }

    [Fact]
    public void DotDotAtTheFilesystemRootDoesNotThrow()
    {
        string top = Path.GetPathRoot(root) ?? throw new InvalidOperationException("the temp directory has no root");
        string typed = Path.Combine(top, "..", "..", "claustrum-no-such-directory", "file.md");

        string resolved = SyncRoot.Resolve(typed);

        Assert.Equal(Path.Combine(top, "claustrum-no-such-directory", "file.md"), resolved, LinkFixtures.PathComparer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASelfLoopSetsLoopedAndCountsAsEscapingOnlyForAFencedRoot(bool absoluteTarget)
    {
        string repo = RealDirectory("repo");
        string loop = Path.Combine(repo, "a");
        LinkFixtures.FileLink(loop, absoluteTarget ? loop : "a");
        string child = Path.Combine(loop, "child.md");

        SyncRoot.Resolve(child, out bool looped);

        Assert.True(looped);
        Assert.True(SyncRoot.Repo(repo).Escapes(child));
        Assert.False(SyncRoot.Global(repo).Escapes(child));
    }

    [Fact]
    public void ATwoLinkCycleLoopsToo()
    {
        string repo = RealDirectory("repo");
        LinkFixtures.FileLink(Path.Combine(repo, "a"), "b");
        LinkFixtures.FileLink(Path.Combine(repo, "b"), "a");

        SyncRoot.Resolve(Path.Combine(repo, "a"), out bool looped);

        Assert.True(looped);
        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "a")));
    }

    [Fact]
    public void APathWithNoLinksDoesNotSetLooped()
    {
        SyncRoot.Resolve(Under("plain", "file.md"), out bool looped);

        Assert.False(looped);
    }

    [Fact]
    public void IsInsideTakesTheRootItselfAndAChildButNotASiblingSharingItsPrefix()
    {
        string repo = RealDirectory("a", "repo");
        string sibling = RealDirectory("a", "repo2");
        SyncRoot fence = SyncRoot.Repo(repo);

        Assert.True(fence.IsInside(repo));
        Assert.True(fence.IsInside(repo + Path.DirectorySeparatorChar));
        Assert.True(fence.IsInside(Path.Combine(repo, "child", "file.md")));
        Assert.False(fence.IsInside(sibling));
        Assert.False(fence.IsInside(Path.Combine(sibling, "file.md")));
        Assert.False(fence.IsInside(Under("a")));
    }

    [Fact]
    public void IsInsideComparesCaseTheWayTheOperatingSystemDoes()
    {
        string repo = RealDirectory("Repo");
        SyncRoot fence = SyncRoot.Repo(repo);
        string shouted = repo.ToUpperInvariant();

        Assert.Equal(OperatingSystem.IsWindows(), fence.IsInside(shouted));
        Assert.Equal(OperatingSystem.IsWindows(), fence.IsInside(Path.Combine(shouted, "child.md")));
    }

    [Fact]
    public void SamePathIgnoresATrailingSeparatorAndComparesCaseTheWayTheOperatingSystemDoes()
    {
        string repo = RealDirectory("repo");

        Assert.True(SyncRoot.SamePath(repo, repo + Path.DirectorySeparatorChar));
        Assert.Equal(OperatingSystem.IsWindows(), SyncRoot.SamePath(repo, repo.ToUpperInvariant()));
    }

    [Fact]
    public void AnUnfencedRootNeverEscapesWhateverTheTargetResolvesTo()
    {
        string repo = RealDirectory("repo");
        string outside = RealDirectory("outside");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "out"), outside);
        SyncRoot global = SyncRoot.Global(repo);

        Assert.False(global.Fenced);
        Assert.False(global.Escapes(Path.Combine(repo, "out", "file.md")));
        Assert.False(global.Escapes(Path.Combine(outside, "file.md")));
        Assert.False(global.Escapes(Path.GetPathRoot(root) ?? root));
    }

    [Fact]
    public void AFencedRootRefusesWhatResolvesOutsideAndAllowsWhatResolvesInside()
    {
        string repo = RealDirectory("repo");
        string outside = RealDirectory("outside");
        RealDirectory("repo", "real");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "out"), outside);
        LinkFixtures.DirectoryLink(Path.Combine(repo, "in"), Path.Combine(repo, "real"));
        SyncRoot fence = SyncRoot.Repo(repo);

        Assert.True(fence.Fenced);
        Assert.True(fence.Escapes(Path.Combine(repo, "out", "file.md")));
        Assert.False(fence.Escapes(Path.Combine(repo, "in", "file.md")));
        Assert.False(fence.Escapes(Path.Combine(repo, "plain", "file.md")));
    }

    [Fact]
    public void ARepoRootThatIsItselfALinkFencesAtItsRealPath()
    {
        string real = RealDirectory("real-repo");
        string linked = Under("linked-repo");
        LinkFixtures.DirectoryLink(linked, real);
        SyncRoot fence = SyncRoot.Repo(linked);

        Assert.Equal(real, fence.RealPath, LinkFixtures.PathComparer);
        Assert.False(fence.Escapes(Path.Combine(linked, "sub", "file.md")));
        Assert.True(fence.Escapes(Under("elsewhere", "file.md")));
    }
}
