using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Backends.Copilot;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests;

// #144, #146: what the user config grants a job dispatched from a directory. The file system is the fake platform's: a dir exists when the test
// says so, so nothing here reads the machine. NOTES.md "Read dirs and the worktree hook".
public sealed class ProjectAccessTests
{
    private static readonly string repos = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-repos"));
    private static readonly string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claustrum-fake-home"));
    private static readonly string userConfig = Path.Combine(home, ".config", "claustrum", "config.json");

    private readonly FakePlatform platform = new() { Os = ClaustrumOs.Linux, HomeDirectory = home };

    private string Exists(params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine([repos, .. parts]));
        platform.ExtraExistingPaths.Add(path);
        return path;
    }

    private string Repo(string name)
    {
        string dir = Exists(name);
        platform.ExtraExistingPaths.Add(Path.Combine(dir, ".git"));
        return dir;
    }

    // A linked worktree `<repos>/<name>` of `main`, the way `git worktree add` writes it: a `.git` file naming a gitdir with a `commondir`.
    private string Linked(string main, string name, string? underMain = null)
    {
        string gitdir = Path.Combine(main, ".git", "worktrees", name);
        platform.ExtraExistingPaths.Add(Path.Combine(main, ".git"));
        platform.Files[Path.Combine(gitdir, "commondir")] = "../..\n";
        string worktree = underMain is null ? Path.Combine(repos, name) : Path.Combine(main, underMain, name);
        platform.ExtraExistingPaths.Add(worktree);
        platform.Files[Path.Combine(worktree, ".git")] = $"gitdir: {gitdir}\n";
        return worktree;
    }

    private static ConfigDocument Document(string[]? readDirs = null, Dictionary<string, ProjectSettings>? projects = null) =>
        new(null, null, null, null, null, readDirs, projects);

    private ProjectAccess Resolve(ConfigDocument document, string cwd) => ProjectAccess.Resolve(document, userConfig, cwd, platform);

    // ---- which projects match ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AGlobMatchesTheMainCheckoutsBasenameAndEveryMatchingProjectAddsItsDirs()
    {
        string altus = Repo("odoo-altus");
        string shared = Exists("shared");
        string devkit = Exists("devkit");
        ConfigDocument document = Document(
            [devkit],
            new Dictionary<string, ProjectSettings>
            {
                ["odoo-*"] = new([shared]),
                ["odoo-altus"] = new([shared, devkit]),
                ["other"] = new([Exists("other-lib")]),
            });

        ProjectAccess access = Resolve(document, altus);

        Assert.Equal("odoo-altus", access.Project);
        Assert.Equal([devkit, shared], access.ConfiguredDirs);
        Assert.Empty(access.Warnings);
        Assert.Empty(access.Issues);
    }

    [Fact]
    public void ALinkedWorktreeAndAJobWorktreeMatchAsTheirMainCheckout()
    {
        string main = Repo("devkit");
        string shared = Exists("shared");
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["devkit"] = new([shared]) });

        ProjectAccess linked = Resolve(document, Linked(main, "devkit-59-fix"));
        ProjectAccess job = Resolve(document, Linked(main, "20261029-092218-3172ff82", underMain: Path.Combine(".claustrum", "worktrees")));

        Assert.Equal("devkit", linked.Project);
        Assert.Equal([shared], linked.ConfiguredDirs);
        Assert.Equal("devkit", job.Project);
        Assert.Equal([shared], job.ConfiguredDirs);
        Assert.Equal(Path.Combine(main, ".git"), linked.GitCommonDir);
    }

    [Fact]
    public void ABareReposWorktreeMatchesOnTheBareReposNameWithoutDotGit()
    {
        string bare = Exists("proj.git");
        platform.Files[Path.Combine(bare, "config")] = "[core]\n\tbare = true\n";
        platform.Files[Path.Combine(bare, "worktrees", "wt", "commondir")] = "../..";
        string worktree = Exists("proj.git", "wt");
        platform.Files[Path.Combine(worktree, ".git")] = $"gitdir: {Path.Combine(bare, "worktrees", "wt")}";
        string shared = Exists("shared");

        ProjectAccess access = Resolve(Document(projects: new Dictionary<string, ProjectSettings> { ["proj"] = new([shared]) }), worktree);

        Assert.Equal("proj", access.Project);
        Assert.Equal([shared], access.ConfiguredDirs);
    }

    [Theory]
    [InlineData(ClaustrumOs.Linux, "Odoo-*", 0)]
    [InlineData(ClaustrumOs.Windows, "Odoo-*", 1)]
    [InlineData(ClaustrumOs.Linux, "odoo-?????", 1)]
    [InlineData(ClaustrumOs.Linux, "odoo-?", 0)]
    public void TheGlobIsCaseSensitiveExceptOnWindowsAndUnderstandsStarAndQuestionMark(ClaustrumOs os, string glob, int dirs)
    {
        FakePlatform here = new() { Os = os, HomeDirectory = home };
        string altus = Path.GetFullPath(Path.Combine(repos, "odoo-altus"));
        string shared = Path.GetFullPath(Path.Combine(repos, "shared"));
        here.ExtraExistingPaths.UnionWith([altus, Path.Combine(altus, ".git"), shared]);

        ProjectAccess access = ProjectAccess.Resolve(Document(projects: new Dictionary<string, ProjectSettings> { [glob] = new([shared]) }), userConfig, altus, here);

        Assert.Equal(dirs, access.ConfiguredDirs.Length);
    }

    [Fact]
    public void OutsideGitNoProjectMatchesButTheGlobalDirsStillApplyAndTheGitCommonDirIsNone()
    {
        string shared = Exists("shared");
        string plain = Exists("not-a-repo");

        ProjectAccess access = Resolve(Document([shared], new Dictionary<string, ProjectSettings> { ["*"] = new([Exists("star")]) }), plain);

        Assert.Null(access.Project);
        Assert.Equal([shared], access.ConfiguredDirs);
        Assert.Null(access.TopLevel);
        Assert.Null(access.GitCommonDir);
        Assert.Empty(access.WorktreeHooks);
    }

    [Fact]
    public void EveryMatchingProjectsHookIsKeptInConfigOrder()
    {
        string altus = Repo("odoo-altus");
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings>
        {
            ["odoo-*"] = new(null, ["chcon", "-R", "{worktree}"]),
            ["nope"] = new(null, ["never"]),
            ["odoo-altus"] = new(null, ["echo", "{worktree}"]),
        });

        ProjectAccess access = Resolve(document, altus);

        Assert.Equal([["chcon", "-R", "{worktree}"], ["echo", "{worktree}"]], access.WorktreeHooks);
    }

    // ---- what is dropped, and said ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingDirIsDroppedWithAWarningAndOneGroupedMissingDirectoryIssueFromDispatch()
    {
        string altus = Repo("odoo-altus");
        string present = Exists("shared");
        string[] gone = [.. Enumerable.Range(1, 7).Select(n => Path.Combine(repos, $"gone{n}"))];

        ProjectAccess access = Resolve(Document([present, .. gone]), altus);

        Assert.Equal([present], access.ConfiguredDirs);
        Assert.Equal(7, access.Warnings.Length);
        Assert.Equal($"read_dirs: {gone[0]} does not exist; the job runs without it", access.Warnings[0]);
        EnvironmentIssue issue = Assert.Single(access.Issues);
        Assert.Equal(EnvironmentIssueKind.MissingDirectory, issue.Kind);
        Assert.Equal("a configured read_dirs directory does not exist", issue.What);
        Assert.Equal($"{gone[0]}, {gone[1]}, {gone[2]}, {gone[3]}, {gone[4]} +2 more", issue.Where);
        Assert.Equal(7, issue.Count);
        Assert.Equal("dispatch", issue.First);
    }

    [Fact]
    public void AFileWhereADirectoryWasConfiguredIsNotADirectory()
    {
        string altus = Repo("odoo-altus");
        string file = Path.Combine(repos, "notes.txt");
        platform.Files[file] = "text";

        ProjectAccess access = Resolve(Document([file]), altus);

        Assert.Empty(access.ConfiguredDirs);
        Assert.Equal($"read_dirs: {file} is not a directory; the job runs without it", Assert.Single(access.Warnings));
        Assert.Equal(EnvironmentIssueKind.MissingDirectory, Assert.Single(access.Issues).Kind);
    }

    // Read-only around the job's own tree would forbid its edits (claude's Edit deny matches the tree too); inside it adds nothing.
    [Fact]
    public void ADirEqualToOrHoldingTheCwdIsDroppedWithAWarningAndOneInsideItIsDroppedSilently()
    {
        string altus = Repo("odoo-altus");
        string inside = Exists("odoo-altus", "addons");
        string keeper = Exists("shared");
        string parent = Path.GetFullPath(repos);
        platform.ExtraExistingPaths.Add(parent);

        ProjectAccess access = Resolve(Document([altus, parent, inside, keeper]), altus);

        Assert.Equal([keeper], access.ConfiguredDirs);
        Assert.Equal(2, access.Warnings.Length);
        Assert.All(access.Warnings, warning => Assert.Contains("holds the working directory", warning, StringComparison.Ordinal));
        Assert.Empty(access.Issues);
    }

    [Fact]
    public void ADirListedTwiceOrByAGlobalAndAProjectIsGivenOnce()
    {
        string altus = Repo("odoo-altus");
        string shared = Exists("shared");

        ProjectAccess access = Resolve(Document([shared, shared + Path.DirectorySeparatorChar], new Dictionary<string, ProjectSettings> { ["odoo-*"] = new([shared]) }), altus);

        Assert.Equal([shared], access.ConfiguredDirs);
    }

    // ---- ~, relative paths and bad entries: refused at dispatch, naming the key, whatever directory the job is dispatched from ----------------

    [Fact]
    public void TildeExpandsToTheHomeDirectoryAloneOrWithAPath()
    {
        string altus = Repo("odoo-altus");
        string shared = Path.Combine(home, "repos", "shared");
        platform.ExtraExistingPaths.UnionWith([home, shared]);

        ProjectAccess access = Resolve(Document(["~", "~/repos/shared"]), altus);

        Assert.Equal([home, shared], access.ConfiguredDirs);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("./shared")]
    [InlineData("../shared")]
    [InlineData("~shared")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARelativeOrEmptyReadDirIsAConfigExceptionNamingTheKeyAndIndex(string entry)
    {
        ConfigException error = Assert.Throws<ConfigException>(() => Resolve(Document([home, entry]), Exists("not-a-repo")));

        Assert.Contains("read_dirs[1]", error.Message, StringComparison.Ordinal);
        Assert.Contains(userConfig, error.Message, StringComparison.Ordinal);
        Assert.Contains("not an absolute path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectsRelativeDirNamesTheProjectGlobInItsKeyEvenWhenTheProjectDoesNotMatch()
    {
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["odoo-*"] = new(["a", "b"]) });

        ConfigException error = Assert.Throws<ConfigException>(() => Resolve(document, Exists("unrelated")));

        Assert.Contains("projects.odoo-*.read_dirs[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectSetToNullIsAConfigExceptionNamingIt()
    {
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["odoo-*"] = null! });

        ConfigException error = Assert.Throws<ConfigException>(() => Resolve(document, Exists("unrelated")));

        Assert.Contains("projects.odoo-* is null", error.Message, StringComparison.Ordinal);
        Assert.Contains(userConfig, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bin/x")]
    [InlineData("./x")]
    [InlineData("../tools/x")]
    [InlineData("sub\\x")]
    public void AHooksRelativeProgramWithASeparatorIsRefusedBecauseItWouldResolveAgainstOurOwnCwd(string program)
    {
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["*"] = new(null, [program, "{worktree}"]) });

        ConfigException error = Assert.Throws<ConfigException>(() => Resolve(document, Exists("unrelated")));

        Assert.Contains("projects.*.worktree_hook[0]", error.Message, StringComparison.Ordinal);
        Assert.Contains("relative path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHookWithNoProgramIsRefused()
    {
        string[][] hooks = [[], [""], ["  ", "x"]];
        foreach (string[] hook in hooks)
        {
            ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["*"] = new(null, hook) });

            ConfigException error = Assert.Throws<ConfigException>(() => Resolve(document, Exists("unrelated")));

            Assert.Contains("projects.*.worktree_hook names no program", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABareProgramNameAndAnAbsolutePathAreAcceptedAsHooks()
    {
        string absolute = Path.GetFullPath(Path.Combine(repos, "tools", "fix"));
        ConfigDocument document = Document(projects: new Dictionary<string, ProjectSettings> { ["a"] = new(null, ["chcon", "-R"]), ["b"] = new(null, [absolute]) });

        ProjectAccess access = Resolve(document, Repo("a"));

        Assert.Equal([["chcon", "-R"]], access.WorktreeHooks);
    }

    // ---- the git common dir ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGitCommonDirIsAddedForARunOutsideItsTreeAndNeverForAMainCheckoutsOwnDotGit()
    {
        string main = Repo("devkit");
        string linked = Linked(main, "devkit-59-fix");
        string shared = Exists("shared");
        ConfigDocument document = Document([shared]);
        string common = Path.Combine(main, ".git");

        Assert.Equal([shared, common], Resolve(document, linked).ReadDirsFor(linked, withGitCommonDir: true));
        Assert.Equal([shared], Resolve(document, linked).ReadDirsFor(linked, withGitCommonDir: false));
        Assert.Equal([shared], Resolve(document, main).ReadDirsFor(main, withGitCommonDir: true));
        Assert.Equal([shared], Resolve(document, Exists("devkit", "src")).ReadDirsFor(main, withGitCommonDir: true));
    }

    [Fact]
    public void TheGitCommonDirIsNotAddedWhenItHoldsTheRunsTreeOrIsAlreadyConfigured()
    {
        string bare = Exists("proj.git");
        platform.Files[Path.Combine(bare, "config")] = "[core]\n\tbare = true\n";
        platform.Files[Path.Combine(bare, "worktrees", "wt", "commondir")] = "../..";
        string worktree = Exists("proj.git", "wt");
        platform.Files[Path.Combine(worktree, ".git")] = $"gitdir: {Path.Combine(bare, "worktrees", "wt")}";
        string main = Repo("devkit");
        string linked = Linked(main, "devkit-59-fix");
        string common = Path.Combine(main, ".git");

        Assert.Empty(Resolve(Document(), worktree).ReadDirsFor(worktree, withGitCommonDir: true));
        Assert.Equal([common], Resolve(Document([common]), linked).ReadDirsFor(linked, withGitCommonDir: true));
    }

    [Fact]
    public void TheRunTopLevelPassedDecidesWhetherTheCommonDirLiesOutsideIt()
    {
        string main = Repo("devkit");
        string job = Linked(main, "20261029-1", underMain: Path.Combine(".claustrum", "worktrees"));
        ProjectAccess fromMain = Resolve(Document(), main);

        // Resolved for the main checkout, the job worktree it will cut is what the run is rooted at: the common dir lies outside it.
        Assert.Equal([Path.Combine(main, ".git")], fromMain.ReadDirsFor(job, withGitCommonDir: true));
        Assert.Empty(fromMain.ReadDirsFor(main, withGitCommonDir: true));
    }

    // ---- ForBackend: what the chosen backend does with the configured dirs --------------------------------------------------------------

    private ProjectAccess WithTwoDirs(params string[] extra)
    {
        string altus = Repo("odoo-altus");
        string[] dirs = [Exists("shared"), Exists("enterprise"), .. extra];
        return Resolve(Document(dirs), altus);
    }

    [Fact]
    public void NoConfiguredDirsMeansNothingIsSaidWhateverTheBackend()
    {
        ProjectAccess none = Resolve(Document(), Repo("odoo-altus"));

        (string[] warnings, EnvironmentIssue[] issues) = none.ForBackend(new ApiBackend(platform), PermissionLevel.EditShell);

        Assert.Empty(warnings);
        Assert.Empty(issues);
    }

    [Fact]
    public void TheApiBackendHasNoFileToolsSoConfiguredDirsAreReportedUnsupportedAtDispatch()
    {
        ProjectAccess access = WithTwoDirs();

        (string[] warnings, EnvironmentIssue[] issues) = access.ForBackend(new ApiBackend(platform), PermissionLevel.ReadOnly);

        Assert.Contains("the api backend cannot give a job extra directories to read", Assert.Single(warnings), StringComparison.Ordinal);
        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal(EnvironmentIssueKind.ReadDirsUnsupported, issue.Kind);
        Assert.Equal("the api backend cannot honour read_dirs", issue.What);
        Assert.Equal(2, issue.Count);
        Assert.Equal("dispatch", issue.First);
    }

    [Theory]
    [InlineData("copilot", PermissionLevel.Edit, true)]
    [InlineData("copilot", PermissionLevel.EditShell, true)]
    [InlineData("copilot", PermissionLevel.ReadOnly, false)]
    [InlineData("copilot", PermissionLevel.Shell, false)]
    [InlineData("copilot", PermissionLevel.Full, false)]
    [InlineData("cursor", PermissionLevel.Edit, true)]
    [InlineData("cursor", PermissionLevel.EditShell, true)]
    [InlineData("cursor", PermissionLevel.ReadOnly, false)]
    [InlineData("cursor", PermissionLevel.Shell, false)]
    [InlineData("claude", PermissionLevel.EditShell, false)]
    [InlineData("opencode", PermissionLevel.EditShell, false)]
    public void CopilotAndCursorGrantTheDirsReadWriteAtTheEditLevelsAndDispatchSaysSo(string backendName, PermissionLevel level, bool readWrite)
    {
        IBackend backend = backendName switch
        {
            "copilot" => new CopilotBackend(platform),
            "cursor" => new CursorBackend(platform),
            "claude" => new ClaudeBackend(platform),
            _ => new OpencodeBackend(platform),
        };
        ProjectAccess access = WithTwoDirs();

        (string[] warnings, EnvironmentIssue[] issues) = access.ForBackend(backend, level);

        Assert.Empty(issues);
        if (readWrite)
        {
            string warning = Assert.Single(warnings);
            Assert.StartsWith($"read_dirs: {backendName} grants read_dirs read-write at level {PermissionLevelParser.ToConfigString(level)}: ", warning, StringComparison.Ordinal);
            Assert.Contains(access.ConfiguredDirs[0], warning, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public void ADirTheHarnessCannotFenceIsLeftOutWarnedAndReportedUnsupportedWhileTheRestAreGiven()
    {
        string odd = Exists("Odoo (copy) 2");
        ProjectAccess access = WithTwoDirs(odd);

        (string[] warnings, EnvironmentIssue[] issues) = access.ForBackend(new ClaudeBackend(platform), PermissionLevel.EditShell);

        Assert.Equal($"read_dirs: claude cannot keep {odd} read-only (its path has ( ) or ,); not given to the job", Assert.Single(warnings));
        EnvironmentIssue issue = Assert.Single(issues);
        Assert.Equal(EnvironmentIssueKind.ReadDirsUnsupported, issue.Kind);
        Assert.Equal("the claude backend cannot fence these read_dirs paths", issue.What);
        Assert.Equal(odd, issue.Where);

        // The same dir is fine where claude only adds it (readonly), and below full opencode is the one that cannot name a glob.
        Assert.Empty(access.ForBackend(new ClaudeBackend(platform), PermissionLevel.ReadOnly).Issues);
        string star = Exists("lib*");
        EnvironmentIssue opencode = Assert.Single(WithTwoDirs(star).ForBackend(new OpencodeBackend(platform), PermissionLevel.ReadOnly).Issues);
        Assert.Equal(star, opencode.Where);
        Assert.Empty(WithTwoDirs(star).ForBackend(new OpencodeBackend(platform), PermissionLevel.Full).Issues);
    }
}
