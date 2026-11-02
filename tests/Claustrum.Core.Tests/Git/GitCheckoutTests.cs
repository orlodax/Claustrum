using Claustrum.Core.Git;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Git;

// #144: where a checkout's git metadata lives, read from files with no git spawned: a plain checkout, a linked worktree (a `.git` FILE naming
// a gitdir whose `commondir` names the shared one), a bare repo with a worktree inside it, a submodule. The layouts are written by hand, the
// way git writes them, so the reader is tested against the format and not against whatever a given git version does.
public sealed class GitCheckoutTests : IDisposable
{
    private static readonly RealPlatform platform = new();

    private readonly string root = Path.GetFullPath(Directory.CreateTempSubdirectory("claustrum-gitcheckout-").FullName);

    public void Dispose() => TempTree.Delete(root);

    private string Dir(params string[] parts)
    {
        string path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void APlainCheckoutHasItsDotGitDirectoryAsCommonDirAndItselfAsMainCheckout()
    {
        string repo = Dir("odoo-altus");
        Dir("odoo-altus", ".git");
        string inner = Dir("odoo-altus", "addons", "x");

        foreach (string start in new[] { repo, inner })
        {
            GitCheckout checkout = GitCheckout.Find(start, platform)!;

            Assert.Equal(repo, checkout.TopLevel);
            Assert.Equal(Path.Combine(repo, ".git"), checkout.CommonDir);
            Assert.Equal(repo, checkout.MainCheckout);
            Assert.False(checkout.Bare);
            Assert.Equal("odoo-altus", checkout.ProjectName);
        }
    }

    [Fact]
    public void OutsideAnyRepositoryThereIsNoCheckout()
    {
        Assert.Null(GitCheckout.Find(Dir("not-a-repo", "deep"), platform));
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\n")]
    [InlineData(true, "\r\n")]
    public void ALinkedWorktreeMatchesAsItsMainCheckoutWhetherItsGitdirIsAbsoluteOrRelativeAndWhateverItsLineEndings(bool relative, string eol)
    {
        string main = Dir("devkit");
        string common = Dir("devkit", ".git");
        string gitdir = Dir("devkit", ".git", "worktrees", "devkit-59-fix");
        WriteFile(Path.Combine(gitdir, "commondir"), $"../..{eol}");
        string worktree = Dir("devkit-59-fix");
        string named = relative ? Path.GetRelativePath(worktree, gitdir) : gitdir;
        WriteFile(Path.Combine(worktree, ".git"), $"gitdir: {named}{eol}");

        GitCheckout checkout = GitCheckout.Find(Path.Combine(worktree, "src"), platform)!;

        Assert.Equal(worktree, checkout.TopLevel);
        Assert.Equal(common, checkout.CommonDir);
        Assert.Equal(main, checkout.MainCheckout);
        Assert.Equal("devkit", checkout.ProjectName);
    }

    [Fact]
    public void AJobWorktreeUnderTheMainCheckoutMatchesAsTheMainCheckoutToo()
    {
        string main = Dir("odoo-altus");
        Dir("odoo-altus", ".git", "worktrees", "20261029-092218-3172ff82");
        WriteFile(Path.Combine(main, ".git", "worktrees", "20261029-092218-3172ff82", "commondir"), "../..\n");
        string job = Dir("odoo-altus", ".claustrum", "worktrees", "20261029-092218-3172ff82");
        WriteFile(Path.Combine(job, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "20261029-092218-3172ff82")}\n");

        GitCheckout checkout = GitCheckout.Find(job, platform)!;

        Assert.Equal(job, checkout.TopLevel);
        Assert.Equal(Path.Combine(main, ".git"), checkout.CommonDir);
        Assert.Equal("odoo-altus", checkout.ProjectName);
    }

    // `git clone --bare proj.git` then `git worktree add proj.git/wt`: the common dir is the bare repo itself, which holds its worktrees,
    // and it is the main checkout; the project name is its directory name without `.git`.
    [Fact]
    public void ABareRepoWithAWorktreeInsideIsItsOwnMainCheckoutAndItsNameLosesDotGit()
    {
        string bare = Dir("proj.git");
        WriteFile(Path.Combine(bare, "config"), "[core]\n\trepositoryformatversion = 0\n\tbare = true\n");
        WriteFile(Path.Combine(bare, "worktrees", "wt", "commondir"), "../..\n");
        string worktree = Dir("proj.git", "wt");
        WriteFile(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(bare, "worktrees", "wt")}\n");

        GitCheckout checkout = GitCheckout.Find(worktree, platform)!;

        Assert.Equal(worktree, checkout.TopLevel);
        Assert.Equal(bare, checkout.CommonDir);
        Assert.Equal(bare, checkout.MainCheckout);
        Assert.True(checkout.Bare);
        Assert.Equal("proj", checkout.ProjectName);
    }

    [Fact]
    public void ABareConfigWithOddSpacingIsStillBare()
    {
        string bare = Dir("spaced.git");
        WriteFile(Path.Combine(bare, "config"), "[core]\r\n\tBare=true\r\n");
        WriteFile(Path.Combine(bare, "worktrees", "wt", "commondir"), "../..");
        string worktree = Dir("spaced.git", "wt");
        WriteFile(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(bare, "worktrees", "wt")}");

        Assert.Equal("spaced", GitCheckout.Find(worktree, platform)!.ProjectName);
    }

    // A submodule's `.git` file names a gitdir under the superproject's `.git/modules`, which has no `commondir`: its own common dir. It is not
    // bare, so the submodule's own top level is its main checkout and its name is the submodule's directory.
    [Fact]
    public void ASubmoduleIsItsOwnMainCheckoutAndItsCommonDirIsTheModulesDirectory()
    {
        string modules = Dir("super", ".git", "modules", "lib");
        WriteFile(Path.Combine(modules, "config"), "[core]\n\tbare = false\n\tworktree = ../../../lib\n");
        string submodule = Dir("super", "lib");
        WriteFile(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");

        GitCheckout checkout = GitCheckout.Find(submodule, platform)!;

        Assert.Equal(submodule, checkout.TopLevel);
        Assert.Equal(modules, checkout.CommonDir);
        Assert.Equal(submodule, checkout.MainCheckout);
        Assert.False(checkout.Bare);
        Assert.Equal("lib", checkout.ProjectName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a gitdir line")]
    [InlineData("gitdir:")]
    [InlineData("gitdir:   ")]
    public void ADotGitFileThatNamesNoGitdirIsNoCheckout(string text)
    {
        string broken = Dir("broken");
        WriteFile(Path.Combine(broken, ".git"), text);

        Assert.Null(GitCheckout.Find(broken, platform));
    }
}
