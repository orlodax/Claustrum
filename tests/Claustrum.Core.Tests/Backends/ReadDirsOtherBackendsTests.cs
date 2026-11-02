using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Api;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Backends.Copilot;
using Claustrum.Core.Backends.Cursor;
using Claustrum.Core.Backends.Opencode;
using Claustrum.Core.Model;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends;

// #144: what copilot, cursor and api do with read dirs, and what each backend declares about them (IBackend.HonoursReadDirs, ReadDirsWritableAt),
// which dispatch reads to tell the user. copilot and cursor take the dir as a whole `--add-dir` argument, so no path character needs refusing.
public sealed class ReadDirsOtherBackendsTests : IDisposable
{
    private static readonly string[] dirs = ["/srv/shared", "/srv/devkit/.git"];

    private readonly string jobDirectory = Directory.CreateTempSubdirectory("claustrum-readdirs-").FullName;
    private readonly string systemPrompt;

    public ReadDirsOtherBackendsTests()
    {
        systemPrompt = Path.Combine(jobDirectory, "system.md");
        File.WriteAllText(systemPrompt, "you are a builder");
    }

    public void Dispose() => Directory.Delete(jobDirectory, recursive: true);

    private ResolvedRun Run(string backend, PermissionLevel level, string[]? readDirs) => new(
        Role: new ResolvedRole("builder", "system body", backend, backend == "api" ? "openrouter:deepseek/deepseek-v4-pro" : "gpt-5", "high", new PermissionPolicy(level, []), Blind: false, HasReport: true),
        Brief: "do the thing",
        Cwd: "/repo",
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: systemPrompt,
        JobDirectory: jobDirectory,
        Env: [],
        ReadDirs: readDirs);

    private static string[] ValuesOf(string[] args, string flag) => [.. args.Select((arg, index) => (arg, index)).Where(pair => pair.arg == flag).Select(pair => args[pair.index + 1])];

    // ---- copilot -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    public void CopilotAddsEachReadDirAfterTheJobsOwnAddDirWhereItVerifiesPaths(PermissionLevel level)
    {
        ProcessSpec spec = new CopilotBackend(new FakePlatform()).Build(Run("copilot", level, dirs));

        Assert.Equal([Path.Combine(jobDirectory, "copilot-agents"), .. dirs], ValuesOf(spec.Args, "--add-dir"));
    }

    // The edit levels already pass --allow-all-paths: every directory is reachable and writable there, so nothing is added (dispatch says so).
    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    [InlineData(PermissionLevel.Full)]
    public void CopilotAddsNothingAtTheLevelsThatAlreadyAllowEveryPath(PermissionLevel level)
    {
        ProcessSpec spec = new CopilotBackend(new FakePlatform()).Build(Run("copilot", level, dirs));

        Assert.Equal([Path.Combine(jobDirectory, "copilot-agents")], ValuesOf(spec.Args, "--add-dir"));
    }

    [Fact]
    public void CopilotWithoutReadDirsIsTheArgvItAlwaysWas()
    {
        CopilotBackend copilot = new(new FakePlatform());

        foreach (PermissionLevel level in Enum.GetValues<PermissionLevel>())
            Assert.Equal(copilot.Build(Run("copilot", level, null)).Args, copilot.Build(Run("copilot", level, [])).Args);
    }

    // ---- cursor ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void CursorAddsAWorkspaceRootPerReadDirRightAfterTheWorkspaceBelowFull(PermissionLevel level)
    {
        ProcessSpec spec = new CursorBackend(new FakePlatform()).Build(Run("cursor", level, dirs));

        int workspace = Array.IndexOf(spec.Args, "--workspace");
        Assert.Equal(["--workspace", "/repo", "--add-dir", dirs[0], "--add-dir", dirs[1]], spec.Args[workspace..]);
    }

    [Fact]
    public void CursorAtFullAndWithoutReadDirsIsUnchanged()
    {
        CursorBackend cursor = new(new FakePlatform());

        Assert.DoesNotContain("--add-dir", cursor.Build(Run("cursor", PermissionLevel.Full, dirs)).Args);
        foreach (PermissionLevel level in Enum.GetValues<PermissionLevel>())
            Assert.DoesNotContain("--add-dir", cursor.Build(Run("cursor", level, null)).Args);
    }

    // ---- api ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ApiHasNoFileToolsSoItDeclaresNoSupportAndBuildsTheSameRequestWithOrWithoutDirs()
    {
        FakePlatform platform = new();
        platform.EnvironmentVariables["OPENROUTER_API_KEY"] = "test-key";
        ApiBackend api = new(platform);
        ResolvedRun with = Run("api", PermissionLevel.ReadOnly, dirs);
        ResolvedRun without = Run("api", PermissionLevel.ReadOnly, null);

        Assert.False(((IBackend)api).HonoursReadDirs);
        Assert.Equal(File.ReadAllText(api.Build(without).TempFiles[0]), File.ReadAllText(api.Build(with).TempFiles[0]));
    }

    // ---- what every backend declares ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryBackendDeclaresWhetherItHonoursReadDirsAndWhereItGrantsThemReadWrite()
    {
        FakePlatform platform = new();
        (IBackend Backend, bool Honours, PermissionLevel[] WritableAt)[] table =
        [
            (new ClaudeBackend(platform), true, []),
            (new OpencodeBackend(platform), true, []),
            (new CopilotBackend(platform), true, [PermissionLevel.Edit, PermissionLevel.EditShell]),
            (new CursorBackend(platform), true, [PermissionLevel.Edit, PermissionLevel.EditShell]),
            (new ApiBackend(platform), false, []),
        ];

        foreach ((IBackend backend, bool honours, PermissionLevel[] writableAt) in table)
        {
            Assert.Equal(honours, backend.HonoursReadDirs);
            foreach (PermissionLevel level in Enum.GetValues<PermissionLevel>())
                Assert.Equal(writableAt.Contains(level), backend.ReadDirsWritableAt(level));
        }
    }

    // A backend must opt in: a new IBackend that implements neither is told at dispatch that it cannot reach the dirs, and refuses nothing.
    [Fact]
    public void ANewBackendThatImplementsNothingAboutReadDirsHonoursNoneAndRefusesNoPath()
    {
        IBackend[] backends = [new ScriptedBackend("true", [])];

        foreach (IBackend backend in backends)
        {
            Assert.False(backend.HonoursReadDirs);
            Assert.False(backend.ReadDirsWritableAt(PermissionLevel.EditShell));
            Assert.Null(backend.ReadDirRefusal("/srv/lib*", PermissionLevel.EditShell));
            Assert.Null(backend.Trace("{}"));
        }
    }

    [Fact]
    public void CursorCopilotAndApiAreNotClassifiedSoTheirReceiptsListDispatchAndReportIssuesOnly()
    {
        FakePlatform platform = new();

        Assert.Null(((IBackend)new CursorBackend(platform)).Trace("{}"));
        Assert.Null(((IBackend)new CopilotBackend(platform)).Trace("{}"));
        Assert.Null(((IBackend)new ApiBackend(platform)).Trace("{}"));
        Assert.NotNull(new ClaudeBackend(platform).Trace(""));
        Assert.NotNull(new OpencodeBackend(platform).Trace(""));
    }
}
