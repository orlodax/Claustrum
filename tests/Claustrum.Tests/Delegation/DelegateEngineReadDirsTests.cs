using System.Text.Json;
using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #144, #145 at the seam where the user config meets a dispatch: DelegateEngine.Prepare turns `read_dirs`, the git common dir and the chosen backend into
// RunOptions.ReadDirs, the dispatch warnings and PreRunIssues. In-process, so the options the run will be handed are visible; the cross-process proof
// is EnvironmentIssuesEndToEndTests. AppServicesHomeFixture keeps the user config this writes in a home of its own, put back after each test.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineReadDirsTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly IsolatedRepo repo = new();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-prepare-").FullName;

    public void Dispose()
    {
        // The home is shared by the collection: the plain, initialized user config is what the next test finds.
        UserConfig.Write(fixture.HomeDirectory);
        repo.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Json(string text) => JsonSerializer.Serialize(text);

    private void WriteUserConfig(string extraKeys) => UserConfig.Write(
        fixture.HomeDirectory,
        "{\"models\":{\"frontier-reasoning\":\"claude:opus\",\"frontier-coding\":\"claude:opus\",\"standard-coding\":\"claude:sonnet\",\"cheap-coding\":\"claude:haiku\",\"fast\":\"claude:haiku\"}," + extraKeys + "}");

    private static DelegateRequest Request(string cwd, string backend = "claude", string? model = null, string[]? warnings = null, string role = "builder") => new(
        Role: role,
        Brief: "do the task",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: backend, Model: model),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        PreRunWarnings: warnings);

    // A real linked worktree of the repository: its .git is a file naming <repo>/.git/worktrees/<name>, so the git common dir lies outside its tree.
    private string LinkedWorktree()
    {
        string linked = Path.Combine(scratch, "linked");
        repo.Git("worktree", "add", "-b", "linked", linked);
        return linked;
    }

    private string CommonDir => Path.Combine(repo.Repo, ".git");

    // ---- the dirs handed to the backend ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AConfiguredDirIsHandedOverAndAMainCheckoutsOwnDotGitIsNot()
    {
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        WriteUserConfig($"\"read_dirs\":[{Json(shared)}]");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(repo.Repo));

        Assert.Equal([shared], prepared.Options.ReadDirs!);
        Assert.Null(prepared.Options.PreRunIssues);
        Assert.Null(prepared.Options.PreRunWarnings);
    }

    [Fact]
    public void ARunInALinkedWorktreeAlsoReadsTheMainCheckoutsGitDirWhereTheBackendKeepsItReadOnly()
    {
        string linked = LinkedWorktree();

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(linked, "claude"));

        Assert.Equal(CommonDir, prepared.Options.ReadDirs!.Single(), ignoreCase: OperatingSystem.IsWindows());
        Assert.Null(prepared.Options.PreRunWarnings);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void TheGitDirIsLeftOutSilentlyWhereTheBackendWouldGrantItReadWriteAtEditShell(string backend)
    {
        string linked = LinkedWorktree();

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(linked, backend, model: "gpt-5"));

        Assert.Null(prepared.Options.ReadDirs);
        Assert.Null(prepared.Options.PreRunWarnings);
        Assert.Null(prepared.Options.PreRunIssues);
    }

    [Fact]
    public void ADirTheBackendGrantsReadWriteIsStillGivenAndSaidSoAtDispatch()
    {
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        WriteUserConfig($"\"read_dirs\":[{Json(shared)}]");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(repo.Repo, "copilot", "gpt-5"));

        Assert.Equal([shared], prepared.Options.ReadDirs!);
        Assert.Equal($"read_dirs: copilot grants read_dirs read-write at level edit+shell: {shared}", Assert.Single(prepared.Options.PreRunWarnings!));
        Assert.Null(prepared.Options.PreRunIssues);
    }

    [Fact]
    public void TheApiBackendCannotReachConfiguredDirsAndDispatchReportsThemUnsupported()
    {
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        WriteUserConfig($"\"read_dirs\":[{Json(shared)}]");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(repo.Repo, "api", "openrouter:deepseek/deepseek-v4-pro", role: "code-reviewer"));

        Assert.Contains("the api backend cannot give a job extra directories to read", Assert.Single(prepared.Options.PreRunWarnings!), StringComparison.Ordinal);
        EnvironmentIssue issue = Assert.Single(prepared.Options.PreRunIssues!);
        Assert.Equal(EnvironmentIssueKind.ReadDirsUnsupported, issue.Kind);
        Assert.Equal("dispatch", issue.First);
    }

    [Fact]
    public void AMissingDirIsAWarningAndAnIssueAfterTheCallersOwnWarnings()
    {
        string gone = Path.Combine(scratch, "gone");
        WriteUserConfig($"\"read_dirs\":[{Json(gone)}]");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(repo.Repo, warnings: ["no git repository at /x"]));

        Assert.Equal(["no git repository at /x", $"read_dirs: {gone} does not exist; the job runs without it"], prepared.Options.PreRunWarnings!);
        Assert.Equal(EnvironmentIssueKind.MissingDirectory, Assert.Single(prepared.Options.PreRunIssues!).Kind);
        Assert.Null(prepared.Options.ReadDirs);
        Assert.Equal(["no git repository at /x"], prepared.Request.PreRunWarnings!);
    }

    [Fact]
    public void ProjectDirsAndHooksComeFromTheMatchingProjectsOfTheUserConfigAndRideOnThePreparedDelegation()
    {
        string shared = Directory.CreateDirectory(Path.Combine(scratch, "shared")).FullName;
        string name = Path.GetFileName(repo.Repo);
        WriteUserConfig($"\"projects\":{{{Json(name)}:{{\"read_dirs\":[{Json(shared)}],\"worktree_hook\":[\"chcon\",\"{{worktree}}\"]}},\"other-*\":{{\"read_dirs\":[{Json(Path.Combine(scratch, "never"))}]}}}}");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(repo.Repo));

        Assert.Equal([shared], prepared.Options.ReadDirs!);
        Assert.Equal([["chcon", "{worktree}"]], prepared.Access!.WorktreeHooks);
        Assert.Equal(name, prepared.Access.Project);
        Assert.Null(prepared.Options.PreRunIssues);
    }

    [Fact]
    public void ARelativeDirIsAConfigExceptionAtPrepareWhateverTheProject()
    {
        WriteUserConfig("\"projects\":{\"nothing-matches-*\":{\"read_dirs\":[\"relative\"]}}");

        ConfigException error = Assert.Throws<ConfigException>(() => DelegateEngine.Prepare(Request(repo.Repo)));

        Assert.Contains("projects.nothing-matches-*.read_dirs[0]", error.Message, StringComparison.Ordinal);
    }
}
