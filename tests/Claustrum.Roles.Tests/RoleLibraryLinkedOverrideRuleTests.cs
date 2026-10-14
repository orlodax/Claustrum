using Claustrum.Roles.Tests.Sync;

namespace Claustrum.Roles.Tests;

// 2026-10-13 review: an override that is a link staying inside the cwd is read only when it leads to a regular file of the
// same extension outside `.git` (rule E), and a pipe or socket standing as an override is a render error, never a read
// that blocks. A link that leaves the cwd is #97's rule and lives in RoleLibraryLocalOverrideTests.
public sealed class RoleLibraryLinkedOverrideRuleTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-role-rule-").FullName;
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-role-rule-out-").FullName;

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(outside);
    }

    private string RoleDirectory => Path.Combine(cwd, ".claustrum", "roles", "builder");

    private string Override(params string[] relative) => Path.Combine([RoleDirectory, .. relative]);

    private string Inside(params string[] relative) => Path.Combine([cwd, .. relative]);

    private void WriteInside(string content, params string[] relative)
    {
        string path = Inside(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? cwd);
        File.WriteAllText(path, content);
    }

    private void LinkOverride(string overridePath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath) ?? cwd);
        LinkFixtures.FileLink(overridePath, target);
    }

    private static void AssertRuleE(RoleRenderException ex) =>
        Assert.Contains("which is not a regular", ex.Message, StringComparison.Ordinal);

    [Fact]
    public void ARoleMdLinkedToGitConfigIsRefused()
    {
        WriteInside("[core]\nsecret = TOKEN\n", ".git", "config");
        LinkOverride(Override("ROLE.md"), Inside(".git", "config"));

        RoleRenderException ex = Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd));

        AssertRuleE(ex);
        Assert.DoesNotContain("TOKEN", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleMdLinkedToAFileWithTheWrongExtensionIsRefused()
    {
        WriteInside("not markdown", "real", "foo.txt");
        LinkOverride(Override("ROLE.md"), Inside("real", "foo.txt"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd)));
    }

    [Fact]
    public void ARoleJsonLinkedToAMarkdownFileIsRefused()
    {
        WriteInside("# not json", "real", "x.md");
        LinkOverride(Override("role.json"), Inside("real", "x.md"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd)));
    }

    [Fact]
    public void APartLinkedIntoGitIsRefused()
    {
        WriteInside("hook body", ".git", "x.md");
        LinkOverride(Override("parts", "delegation.claude.md"), Inside(".git", "x.md"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().ReadPart("builder", "delegation", "claude", cwd)));
    }

    [Fact]
    public void APartLinkedIntoAGitDirectoryAnywhereInTheTreeIsRefused()
    {
        WriteInside("hook body", "sub", ".GIT", "x.md");
        LinkOverride(Override("parts", "delegation.claude.md"), Inside("sub", ".GIT", "x.md"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().ReadPart("builder", "delegation", "claude", cwd)));
    }

    [Fact]
    public void ADanglingLinkThatStaysInsideIsRefusedWithTheRuleMessage()
    {
        LinkOverride(Override("ROLE.md"), Inside("not-there.md"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd)));
    }

    // The link is a directory link whose target is `.git`: the typed ROLE.md is a plain name under a link into `.git`.
    [Fact]
    public void ADirectoryLinkMidPathIntoGitIsRefused()
    {
        WriteInside("# stolen", ".git", "refs", "ROLE.md");
        Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "roles"));
        LinkFixtures.DirectoryLink(RoleDirectory, Inside(".git", "refs"));

        AssertRuleE(Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd)));
    }

    [Fact]
    public void ARoleMdLinkedToAnotherRoleMdInsideTheRepoIsRead()
    {
        WriteInside("You are the INSIDE builder.\n", "inside", "ROLE.md");
        LinkOverride(Override("ROLE.md"), Inside("inside", "ROLE.md"));

        LoadedRole loaded = new RoleLibrary().LoadRole("builder", cwd);

        Assert.Equal("You are the INSIDE builder.\n", loaded.RoleMdTemplate);
        Assert.True(loaded.IsLocalOverride);
    }

    [Fact]
    public void APartLinkedToAMarkdownFileInsideIsRead()
    {
        WriteInside("INSIDE-PART", "inside", "p.md");
        LinkOverride(Override("parts", "delegation.claude.md"), Inside("inside", "p.md"));

        Assert.Equal("INSIDE-PART", new RoleLibrary().ReadPart("builder", "delegation", "claude", cwd));
    }

    // HasLocalOverride is a probe that must not touch the disk beyond the fence: an escaping link is "something is there".
    [Theory]
    [InlineData("role.json")]
    [InlineData("ROLE.md")]
    public void HasLocalOverrideIsTrueForAnEscapingOverrideLinkWithoutReadingIt(string file)
    {
        string secret = Path.Combine(outside, "secret");
        File.WriteAllText(secret, "TOKEN");
        LinkOverride(Override(file), secret);

        Assert.True(RoleLibrary.HasLocalOverride("builder", cwd));
    }

    [Fact]
    public void HasLocalOverrideIsTrueForAnEscapingPartsDirectoryLink()
    {
        Directory.CreateDirectory(RoleDirectory);
        File.WriteAllText(Path.Combine(outside, "p.md"), "x");
        LinkFixtures.DirectoryLink(Override("parts"), outside);

        Assert.True(RoleLibrary.HasLocalOverride("builder", cwd));
    }

    [Theory]
    [InlineData("ROLE.md")]
    [InlineData("role.json")]
    [InlineData("parts/delegation.claude.md")]
    public void AFifoStandingAsAnOverrideIsARenderErrorNotAHang(string relative)
    {
        string path = Override(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? cwd);
        PipeFiles.MakeFifo(path);

        RoleRenderException ex = PipeFiles.Within(() => Assert.Throws<RoleRenderException>(() =>
        {
            RoleLibrary library = new();
            return relative.StartsWith("parts", StringComparison.Ordinal)
                ? library.ReadPart("builder", "delegation", "claude", cwd)
                : library.LoadRole("builder", cwd).RoleMdTemplate;
        }));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundSocketStandingAsAnOverrideIsARenderError()
    {
        Directory.CreateDirectory(RoleDirectory);
        using System.Net.Sockets.Socket socket = PipeFiles.BindSocket(Override("ROLE.md"));

        RoleRenderException ex = PipeFiles.Within(() => Assert.Throws<RoleRenderException>(() => new RoleLibrary().LoadRole("builder", cwd)));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }
}
