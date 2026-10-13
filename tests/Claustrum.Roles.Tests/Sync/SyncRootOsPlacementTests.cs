using System.Runtime.Versioning;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #100, #101: on Windows a link the string walk cannot place (an NT-namespace target, a folder-mounted volume, an unreadable
// reparse point) is placed by the OS, and the answer is compared with the OS's spelling of the root, never the typed one
// (WindowsFinalPath is internal: SyncRoot.Resolve/Escapes are the seam). A component whose link read throws is opaque, never a
// plain directory. A folder-mounted volume and a container bind mount cannot be made on a CI runner; the branch that decides
// them (an opaque component) is the same one the NT-namespace links and the chmod 000 case below take.
public sealed class SyncRootOsPlacementTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("claustrum-osplace-").FullName;

    public void Dispose() => LinkFixtures.DeleteTree(root);

    private static string Nt(string path) => @"\\?\GLOBALROOT\??\" + path;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("an NT-namespace link target and the OS's own placement of it are Windows-only.");
    }

    private string Under(params string[] parts) => Path.Combine([root, .. parts]);

    private string RealDirectory(params string[] parts) => Directory.CreateDirectory(Under(parts)).FullName;

    // #100: `.claude` stored as a `\??\GLOBALROOT\??\<repo>\real-claude` link used to be refused as unplaceable; the OS places it
    // inside the repo. The temp root is typed 8.3 (`RUNNER~1`) on the CI runner while the OS answers with the long name, so this
    // also proves both sides of the comparison are the OS's. Tails are compared, never the whole path.
    [Fact]
    public void AnNtNamespaceDirectoryLinkWhoseTargetLiesInsideTheRepoIsPlacedByTheOsAndNotRefusedOnWindows()
    {
        RequireWindows();
        string repo = RealDirectory("repo");
        string realClaude = RealDirectory("repo", "real-claude");
        LinkFixtures.DirectoryLink(Path.Combine(repo, ".claude"), Nt(realClaude));
        string path = Path.Combine(repo, ".claude", "agents", "builder.md");
        SyncRoot fence = SyncRoot.Repo(repo);

        // Nothing below the link exists yet: the OS places the link, and the names under it ride along as typed.
        string missingTail = SyncRoot.Resolve(path, out bool unresolvedMissing);
        Assert.False(fence.Escapes(path));
        Assert.False(unresolvedMissing);
        Assert.EndsWith(Path.Combine("real-claude", "agents", "builder.md"), missingTail, StringComparison.OrdinalIgnoreCase);

        Directory.CreateDirectory(Path.Combine(realClaude, "agents"));
        File.WriteAllText(Path.Combine(realClaude, "agents", "builder.md"), "mine\n");
        string existing = SyncRoot.Resolve(path, out bool unresolvedExisting);
        Assert.False(fence.Escapes(path));
        Assert.False(unresolvedExisting);
        Assert.EndsWith(Path.Combine("real-claude", "agents", "builder.md"), existing, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnNtNamespaceFileLinkToAFileInsideTheRepoIsPlacedByTheOsAndNotRefusedOnWindows()
    {
        RequireWindows();
        string repo = RealDirectory("repo");
        string real = RealDirectory("repo", "real");
        File.WriteAllText(Path.Combine(real, "mcp.json"), "{}");
        LinkFixtures.FileLink(Path.Combine(repo, ".mcp.json"), Nt(Path.Combine(real, "mcp.json")));
        string path = Path.Combine(repo, ".mcp.json");

        string resolved = SyncRoot.Resolve(path, out bool unresolved);

        Assert.False(SyncRoot.Repo(repo).Escapes(path));
        Assert.False(unresolved);
        Assert.EndsWith(Path.Combine("real", "mcp.json"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    // The repo root itself behind an NT-namespace link: RealPath and the candidate are both the OS's answer.
    [Fact]
    public void ARepoRootBehindAnNtNamespaceLinkFencesAtTheOsPlacedPathOnWindows()
    {
        RequireWindows();
        string realRepo = RealDirectory("real-repo");
        string outside = RealDirectory("outside");
        string linked = Under("linked-repo");
        LinkFixtures.DirectoryLink(linked, Nt(realRepo));
        SyncRoot fence = SyncRoot.Repo(linked);

        Assert.False(fence.Escapes(Path.Combine(linked, "sub", "file.md")));
        Assert.True(fence.Escapes(Path.Combine(outside, "file.md")));
    }

    // The OS is asked about the whole typed path, not a half-spliced one: a readable link ahead of the opaque one changes nothing.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnOpaqueLinkReachedThroughAReadableLinkIsPlacedFromTheTypedPathOnWindows(bool junction, bool targetInsideTheRepo)
    {
        RequireWindows();
        string repo = RealDirectory("repo");
        string hop = RealDirectory("repo", "hop");
        string inside = RealDirectory("repo", "real");
        string outside = RealDirectory("outside");
        LinkFixtures.DirectoryLink(Path.Combine(hop, "nt"), Nt(targetInsideTheRepo ? inside : outside));
        string entry = Path.Combine(repo, "entry");
        if (junction)
            LinkFixtures.Junction(entry, hop);
        else
            LinkFixtures.DirectoryLink(entry, hop);

        string path = Path.Combine(entry, "nt", "file.md");

        Assert.Equal(!targetInsideTheRepo, SyncRoot.Repo(repo).Escapes(path));
        string resolved = SyncRoot.Resolve(path, out bool unresolved);
        Assert.False(unresolved);
        Assert.EndsWith(Path.Combine(targetInsideTheRepo ? "real" : "outside", "file.md"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    // Writing through a dangling file link creates its target, so the OS cannot "place" it and the fence must not pop its name as
    // a missing tail. The refusal line shows the raw target, because the typed path there would claim the fence resolved it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADanglingNtNamespaceFileLinkIsRefusedAndDisplayedAsItsRawTargetOnWindows(bool targetInsideTheRepo)
    {
        RequireWindows();
        string repo = RealDirectory("repo");
        string directory = targetInsideTheRepo ? RealDirectory("repo", "real") : RealDirectory("outside");
        string link = Path.Combine(repo, ".mcp.json");
        LinkFixtures.FileLink(link, Nt(Path.Combine(directory, "missing.json")));

        string resolved = SyncRoot.Resolve(link, out bool unresolved);

        Assert.True(SyncRoot.Repo(repo).Escapes(link));
        Assert.True(unresolved);
        Assert.EndsWith("missing.json", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("GLOBALROOT", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.False(SyncRoot.Global(repo).Escapes(link));
    }

    // The device does not exist, so there is nothing for the OS to place: unresolved, shown as the raw target plus the rest.
    [Fact]
    public void AnUnplaceableLinkIsDisplayedAsItsRawTargetPlusTheRestOnWindows()
    {
        RequireWindows();
        string repo = RealDirectory("repo");
        string link = Path.Combine(repo, "opaque");
        LinkFixtures.DirectoryLink(link, @"\\?\Global\NoSuchDevice\claustrum");

        string resolved = SyncRoot.Resolve(Path.Combine(link, "sub", "file.md"), out bool unresolved);

        Assert.True(unresolved);
        Assert.Contains(Path.Combine("NoSuchDevice", "claustrum"), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("claustrum", "sub", "file.md"), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(repo, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(SyncRoot.Repo(repo).Escapes(Path.Combine(link, "sub", "file.md")));
    }

    // #101, Unix: `readlink` on a name inside a directory the account cannot search throws, and a throw is "opaque", never "no
    // link": the fence cannot prove the path stays inside, so it is refused. The display falls back to the typed path, because
    // no target was readable.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ADirectoryOnTheWayThatCannotBeSearchedMakesThePathUnresolvedAndRefusedOnUnix()
    {
        string repo = RealDirectory("repo");
        string claude = RealDirectory("repo", ".claude");
        string path = Path.Combine(repo, ".claude", "agents", "builder.md");
        using IDisposable locked = LinkFixtures.Lock(claude);

        string resolved = SyncRoot.Resolve(path, out bool unresolved);

        Assert.True(unresolved);
        Assert.Equal(path, resolved);
        Assert.True(SyncRoot.Repo(repo).Escapes(path));
        Assert.False(SyncRoot.Global(repo).Escapes(path));
    }

    // The unreadable directory is the leaf's parent: the directory itself is still a plain directory to its own parent.
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void TheUnsearchableDirectoryItselfIsInsideButWhatItHoldsIsRefusedOnUnix()
    {
        string repo = RealDirectory("repo");
        string claude = RealDirectory("repo", ".claude");
        using IDisposable locked = LinkFixtures.Lock(claude);
        SyncRoot fence = SyncRoot.Repo(repo);

        Assert.False(fence.Escapes(claude));
        Assert.True(fence.Escapes(Path.Combine(claude, "agents")));
    }

    // Guards the other side of #101: a file the account cannot read is still a plain file, not an opaque component.
    [Fact]
    public void AnUnreadableFileIsNotALinkAndStaysInsideOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX file modes do not exist on Windows.");
            return;
        }

        string repo = RealDirectory("repo");
        string file = Path.Combine(repo, "private.json");
        File.WriteAllText(file, "{}");
        File.SetUnixFileMode(file, UnixFileMode.None);

        Assert.Equal(file, SyncRoot.Resolve(file, out bool unresolved));
        Assert.False(unresolved);
        Assert.False(SyncRoot.Repo(repo).Escapes(file));
    }

    // The `-1` trap (#101): a path that does not exist reads (FileAttributes)(-1), every bit set, ReparsePoint included, and
    // must stay a plain missing tail. Under a regular file the answer is the same on Unix (ENOTDIR); Windows' error code for
    // that shape is not measured, so the case runs there only through the missing-tail guard in SyncRootTests.
    [Fact]
    public void ANameUnderARegularFileIsAMissingTailAndNotAnOpaqueComponentOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("ENOTDIR is a Unix error; SyncRootTests guards the missing tail on Windows.");
            return;
        }

        string repo = RealDirectory("repo");
        string file = Path.Combine(repo, "afile");
        File.WriteAllText(file, "x");
        string under = Path.Combine(file, "child", "grandchild.md");

        Assert.Equal(under, SyncRoot.Resolve(under, out bool unresolved));
        Assert.False(unresolved);
        Assert.False(SyncRoot.Repo(repo).Escapes(under));
    }
}
