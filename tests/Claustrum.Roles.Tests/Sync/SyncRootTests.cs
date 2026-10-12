using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #95: SyncRoot.Resolve follows every link on the way (directory and leaf alike) and leaves a tail that does not exist as
// typed; Escapes is the fence. `..` after a link is physical on Unix, as the kernel applies it, and lexical on Windows.
public sealed class SyncRootTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-syncroot-").FullName;

    public void Dispose() => LinkFixtures.DeleteTree(root);

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

    // Windows reads `d\..` lexically (so the repo), but NTFS may apply it physically; the second walk is what refuses it.
    [Fact]
    public void DotDotAfterALinkIsRefusedOnWindowsByThePhysicalWalk()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("the second, physical `..` walk runs on Windows only.");
            return;
        }

        string repo = RealDirectory("repo");
        string outsideAgents = RealDirectory("outside", "agents");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "d"), outsideAgents);
        LinkFixtures.DirectoryLink(Path.Combine(repo, "x"), Path.Combine("d", ".."));
        string path = Path.Combine(repo, "x", "file");
        SyncRoot fence = SyncRoot.Repo(repo);

        Assert.True(fence.IsInside(SyncRoot.Resolve(path)));
        Assert.True(fence.Escapes(path));
    }

    [Fact]
    public void AJunctionIsFollowedLikeADirectoryLinkOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("a junction is a Windows directory-link kind.");
            return;
        }

        string repo = RealDirectory("repo");
        string outside = RealDirectory("outside");
        string real = RealDirectory("repo", "real");
        LinkFixtures.Junction(Path.Combine(repo, "out"), outside);
        LinkFixtures.Junction(Path.Combine(repo, "in"), real);
        string outPath = Path.Combine(repo, "out", "file.md");

        Assert.Equal(Path.Combine(outside, "file.md"), SyncRoot.Resolve(outPath), LinkFixtures.PathComparer);
        Assert.True(SyncRoot.Repo(repo).Escapes(outPath));
        Assert.False(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "in", "file.md")));
        Assert.False(SyncRoot.Global(repo).Escapes(outPath));
    }

    // .NET strips only `\??\` from an absolute reparse target, so these come back looking relative (#95 review).
    [Theory]
    [InlineData(@"GLOBALROOT\Device\HarddiskVolume3\Users\me\.claude")]
    [InlineData(@"Volume{01234567-89ab-cdef-0123-456789abcdef}\Users\me\.claude")]
    [InlineData(@"GLOBALROOT\??\C:\Users\me\.claude")]
    [InlineData(@"C:foo")]
    [InlineData(@"a?b\c")]
    public void IsOpaqueWindowsLinkTargetFlagsTheNtNamespaceFormsAndNotOrdinaryTargets(string target)
    {
        Assert.True(SyncRoot.IsOpaqueWindowsLinkTarget(target));
    }

    [Theory]
    [InlineData(@"..\..\notes")]
    [InlineData(@"sub\dir")]
    [InlineData("plain")]
    public void IsOpaqueWindowsLinkTargetLeavesAnOrdinaryRelativeTargetAlone(string target)
    {
        Assert.False(SyncRoot.IsOpaqueWindowsLinkTarget(target));
    }

    // Fully qualified, so the inside test already refuses them; the predicate must not flag them.
    [Theory]
    [InlineData(@"C:\Users\me\.claude")]
    [InlineData(@"\\?\C:\Users\me\.claude")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\x")]
    public void IsOpaqueWindowsLinkTargetLeavesAFullyQualifiedWindowsTargetAlone(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("these are fully qualified only where Windows drive letters and the extended-length prefix exist.");
            return;
        }

        Assert.False(SyncRoot.IsOpaqueWindowsLinkTarget(target));
    }

    [Fact]
    public void AGlobalRootLinkTargetIsRefusedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("an NT-namespace link target is a Windows form.");
            return;
        }

        string repo = RealDirectory("repo");
        string outside = RealDirectory("outside");
        string link = Path.Combine(repo, "out");
        LinkFixtures.DirectoryLink(link, @"\\?\GLOBALROOT\??\" + outside);
        string path = Path.Combine(link, "file.md");

        Assert.True(SyncRoot.Repo(repo).Escapes(path));
        SyncRoot.Resolve(path, out bool unresolved);
        if (!Path.IsPathFullyQualified(new DirectoryInfo(link).LinkTarget ?? ""))
            Assert.True(unresolved);

        Assert.False(SyncRoot.Global(repo).Escapes(path));
    }

    // Off Windows a POSIX link is followed whatever its target string looks like: `a?b:c` trips the name list, yet is an ordinary name here.
    [Fact]
    public void TheWindowsOpaqueChecksAreNotAppliedOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("off-Windows behaviour: the walk applies the opaque checks on Windows only.");
            return;
        }

        string repo = RealDirectory("repo");
        RealDirectory("repo", "a?b:c", "sub");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "x"), "a?b:c");
        string path = Path.Combine(repo, "x", "sub", "file.md");

        Assert.True(SyncRoot.IsOpaqueWindowsLinkTarget("a?b:c"));
        Assert.Equal(Path.Combine(repo, "a?b:c", "sub", "file.md"), SyncRoot.Resolve(path, out bool unresolved));
        Assert.False(unresolved);
        Assert.False(SyncRoot.Repo(repo).Escapes(path));
    }

    // The structural rule (#95 review): ResolveLinkTarget(false).ToString() that is not fully qualified is an absolute
    // NT-namespace target. The nt and Global links are not guarded on LinkTarget's shape: if the runtime returned a
    // re-rooted path this fails on the Windows CI leg instead of skipping.
    [Fact]
    public void IsOpaqueWindowsLinkSeesAnNtNamespaceTargetAndNotAnOrdinaryOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("an NT-namespace link target is a Windows form.");
            return;
        }

        string repo = RealDirectory("repo");
        string outside = RealDirectory("outside");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "rel"), Path.Combine("..", "outside"));
        LinkFixtures.DirectoryLink(Path.Combine(repo, "abs"), outside);
        LinkFixtures.DirectoryLink(Path.Combine(repo, "nt"), @"\\?\GLOBALROOT\??\" + outside);
        LinkFixtures.DirectoryLink(Path.Combine(repo, "global"), @"\\?\Global\GLOBALROOT\??\" + outside);

        Assert.False(SyncRoot.IsOpaqueWindowsLink(Path.Combine(repo, "rel")));
        Assert.False(SyncRoot.IsOpaqueWindowsLink(Path.Combine(repo, "abs")));
        Assert.True(SyncRoot.IsOpaqueWindowsLink(Path.Combine(repo, "nt")));
        Assert.True(SyncRoot.IsOpaqueWindowsLink(Path.Combine(repo, "global")));
        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "nt", "file.md")));
        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "global", "file.md")));
    }

    // The name list sees neither `?` nor `:` nor GLOBALROOT/Volume{ in `Global\NoSuchDevice\claustrum`, so only the
    // structural check can refuse it; deleting `|| IsOpaqueWindowsLink(info)` from the walk fails this test.
    [Fact]
    public void TheStructuralCheckRefusesAnNtTargetTheNameListDoesNotSeeOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("an NT-namespace link target is a Windows form.");
            return;
        }

        string repo = RealDirectory("repo");
        string link = Path.Combine(repo, "opaque");
        LinkFixtures.DirectoryLink(link, @"\\?\Global\NoSuchDevice\claustrum");
        string path = Path.Combine(link, "file.md");

        // The link dangles, so Directory.Exists is false and FileInfo reads it, as SyncRoot.InfoOf does.
        string? target = (Directory.Exists(link) ? new DirectoryInfo(link) : (FileSystemInfo)new FileInfo(link)).LinkTarget;

        Assert.NotNull(target);
        Assert.False(SyncRoot.IsOpaqueWindowsLinkTarget(target));
        Assert.True(SyncRoot.IsOpaqueWindowsLink(link));
        SyncRoot.Resolve(path, out bool unresolved);
        Assert.True(unresolved);
        Assert.True(SyncRoot.Repo(repo).Escapes(path));
        Assert.False(SyncRoot.Global(repo).Escapes(path));
    }

    // `\\?\` is stored as `\??\` and LinkTarget reads it as "": the structural read throws, and a throw is opaque, not a crash.
    [Fact]
    public void AnEmptyNtTargetIsOpaqueNotACrashOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("an NT-namespace link target is a Windows form.");
            return;
        }

        string repo = RealDirectory("repo");
        LinkFixtures.DirectoryLink(Path.Combine(repo, "empty"), @"\\?\");

        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(repo, "empty", "file.md")));
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
