using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Claude;

// #128: a claude role runs with --strict-mcp-config and, at most, a checked copy of the cwd's .mcp.json in the job directory.
// These run against the real filesystem (RealPlatform) because the judgement is about links, FIFOs and bytes on disk. The
// link and FIFO cases are Unix only: a Windows runner may not be allowed to create a symlink, and has no FIFO.
public sealed class ClaudeBackendMcpTests : IDisposable
{
    private const string Servers = /*lang=json,strict*/ """{"mcpServers":{"docs":{"command":"docs-mcp"},"tracker":{"command":"tracker-mcp"}}}""";

    private readonly ScratchRoot scratch = new("claustrum-mcp-");
    private readonly ClaudeBackend backend = new(new RealPlatform());
    private readonly string cwd;
    private readonly string jobDirectory;

    public ClaudeBackendMcpTests()
    {
        cwd = scratch.CreateDirectory();
        jobDirectory = scratch.CreateDirectory();
    }

    public void Dispose() => scratch.Dispose();

    private string Mcp => Path.Combine(cwd, ".mcp.json");

    private string Copy => Path.Combine(jobDirectory, "mcp.json");

    private ProcessSpec Build(bool userMcp = false, PermissionLevel level = PermissionLevel.EditShell) => backend.Build(new ResolvedRun(
        Role: new ResolvedRole("builder", "system body", "claude", "sonnet", "high", new PermissionPolicy(level, []), Blind: false, HasReport: true, UserMcp: userMcp),
        Brief: "do the thing",
        Cwd: cwd,
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: Path.Combine(jobDirectory, "system.md"),
        JobDirectory: jobDirectory,
        Env: []));

    private static void LinkOrSkip(string link, string target)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("creating a symlink needs a privilege a Windows runner does not always grant.");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }
    }

    private static string[] ServersOf(ProcessSpec spec) => spec.McpServers ?? throw new Xunit.Sdk.XunitException("the backend left McpServers null");

    private static void AssertRefused(ProcessSpec spec)
    {
        Assert.Contains("--strict-mcp-config", spec.Args);
        Assert.DoesNotContain("--mcp-config", spec.Args);
        Assert.Equal([], ServersOf(spec));
        string warning = Assert.Single(spec.Warnings ?? []);
        Assert.Contains(".mcp.json not given to claude", warning, StringComparison.Ordinal);
        Assert.Contains("the run has no MCP servers", warning, StringComparison.Ordinal);
    }

    // ---- the flag ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    [InlineData(PermissionLevel.Full)]
    public void StrictMcpConfigComesRightAfterTheSystemPromptFileAndBeforeThePermissionArgs(PermissionLevel level)
    {
        ProcessSpec spec = Build(level: level);

        int prompt = Array.IndexOf(spec.Args, "--append-system-prompt-file");
        int strict = Array.IndexOf(spec.Args, "--strict-mcp-config");
        int permission = Array.FindIndex(spec.Args, arg => arg is "--permission-mode" or "--dangerously-skip-permissions");
        Assert.Equal(prompt + 2, strict);
        Assert.Equal(strict + 1, permission);
        Assert.Single(spec.Args, arg => arg == "--strict-mcp-config");
    }

    [Fact]
    public void NoMcpJsonMeansNoMcpConfigArgAndNoServersAndNoWarning()
    {
        ProcessSpec spec = Build();

        Assert.Contains("--strict-mcp-config", spec.Args);
        Assert.DoesNotContain("--mcp-config", spec.Args);
        Assert.Equal([], ServersOf(spec));
        Assert.Null(spec.Warnings);
        Assert.False(File.Exists(Copy));
    }

    [Fact]
    public void ReadOnlyKeepsItsAllowedToolsAndPermissionArgsUnchanged()
    {
        File.WriteAllText(Mcp, Servers);

        ProcessSpec spec = Build(level: PermissionLevel.ReadOnly);

        int mode = Array.IndexOf(spec.Args, "--permission-mode");
        Assert.Equal(
        [
            "--permission-mode", "plan", "--permission-prompts", "none",
            "--allowedTools", "Read,Glob,Grep,Bash(git diff*),Bash(git log*),Bash(git show*),Bash(git status*),Bash(gh pr *),Bash(gh issue *)",
            "--no-session-persistence",
            "--effort", "high",
        ], spec.Args[mode..]);
    }

    // ---- a regular .mcp.json -----------------------------------------------------------------------------------------

    [Fact]
    public void ARegularMcpJsonIsCopiedIntoTheJobDirectoryAndThatCopyIsWhatClaudeIsGiven()
    {
        File.WriteAllText(Mcp, Servers);

        ProcessSpec spec = Build();

        int flag = Array.IndexOf(spec.Args, "--mcp-config");
        Assert.True(flag > Array.IndexOf(spec.Args, "--strict-mcp-config"));
        Assert.Equal(Copy, spec.Args[flag + 1]);
        Assert.Equal(Servers, File.ReadAllText(Copy));
        Assert.Equal(["docs", "tracker"], ServersOf(spec));
        Assert.Null(spec.Warnings);
    }

    [Fact]
    public void ACommentedFileWithTrailingCommasIsAcceptedAndCopiedByteForByte()
    {
        const string text = /*lang=json*/ """
            // the repo's servers
            {
              /* one */
              "mcpServers": { "docs": { "command": "docs-mcp", }, },
            }
            """;
        File.WriteAllText(Mcp, text);

        ProcessSpec spec = Build();

        Assert.Equal(Copy, spec.Args[Array.IndexOf(spec.Args, "--mcp-config") + 1]);
        Assert.Equal(text, File.ReadAllText(Copy));
        Assert.Equal(["docs"], ServersOf(spec));
        Assert.Null(spec.Warnings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(/*lang=json,strict*/ """{"mcpServers":[]}""")]
    [InlineData("[1,2]")]
    public void AValidFileWithoutAnMcpServersObjectIsStillPassedAndListsNoServers(string text)
    {
        File.WriteAllText(Mcp, text);

        ProcessSpec spec = Build();

        Assert.Contains("--mcp-config", spec.Args);
        Assert.Equal([], ServersOf(spec));
        Assert.Null(spec.Warnings);
    }

    [Fact]
    public void AnInRepoLinkToARegularFileIsAccepted()
    {
        Directory.CreateDirectory(Path.Combine(cwd, "sub"));
        File.WriteAllText(Path.Combine(cwd, "sub", "m.json"), Servers);
        LinkOrSkip(Mcp, Path.Combine("sub", "m.json"));

        ProcessSpec spec = Build();

        Assert.Equal(Copy, spec.Args[Array.IndexOf(spec.Args, "--mcp-config") + 1]);
        Assert.Equal(Servers, File.ReadAllText(Copy));
        Assert.Equal(["docs", "tracker"], ServersOf(spec));
        Assert.Null(spec.Warnings);
    }

    // ---- refusals ----------------------------------------------------------------------------------------------------

    [Fact]
    public void MalformedJsonIsRefusedWithOneWarning()
    {
        File.WriteAllText(Mcp, """{"mcpServers": {""");

        ProcessSpec spec = Build();

        AssertRefused(spec);
        Assert.Contains("could not be parsed", spec.Warnings![0], StringComparison.Ordinal);
        Assert.False(File.Exists(Copy));
    }

    [Fact]
    public void AFifoIsRefusedWithoutBlocking()
    {
        PipeFiles.MakeFifo(Mcp);

        ProcessSpec spec = PipeFiles.Within(() => Build());

        AssertRefused(spec);
        Assert.False(File.Exists(Copy));
    }

    [Fact]
    public void ALinkToAFileOutsideTheWorkingDirectoryIsRefused()
    {
        string outside = Path.Combine(scratch.CreateDirectory(), "elsewhere.json");
        File.WriteAllText(outside, Servers);
        LinkOrSkip(Mcp, outside);

        ProcessSpec spec = Build();

        AssertRefused(spec);
        Assert.Contains("outside the working directory", spec.Warnings![0], StringComparison.Ordinal);
        Assert.False(File.Exists(Copy));
    }

    // A `..` is refused even when the resolved target is inside the repo: collapsing it would hide a link on the way.
    [Fact]
    public void ALinkWhoseTargetContainsDotDotIsRefusedEvenInsideTheRepo()
    {
        Directory.CreateDirectory(Path.Combine(cwd, "sub"));
        File.WriteAllText(Path.Combine(cwd, "m.json"), Servers);
        LinkOrSkip(Mcp, Path.Combine("sub", "..", "m.json"));

        ProcessSpec spec = Build();

        AssertRefused(spec);
        Assert.Contains("'..'", spec.Warnings![0], StringComparison.Ordinal);
        Assert.False(File.Exists(Copy));
    }

    [Fact]
    public void ADirectorySymlinkOnTheWayToTheFileIsRefused()
    {
        string real = Path.Combine(cwd, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "m.json"), Servers);
        if (OperatingSystem.IsWindows())
            Assert.Skip("creating a symlink needs a privilege a Windows runner does not always grant.");

        Directory.CreateSymbolicLink(Path.Combine(cwd, "linked"), real);
        LinkOrSkip(Mcp, Path.Combine("linked", "m.json"));

        ProcessSpec spec = Build();

        AssertRefused(spec);
        Assert.Contains("on the way to it is a link", spec.Warnings![0], StringComparison.Ordinal);
        Assert.False(File.Exists(Copy));
    }

    [Fact]
    public void AChainOfMoreThanFortyLinksIsRefused()
    {
        File.WriteAllText(Path.Combine(cwd, "end.json"), Servers);
        const int links = 45;
        for (int i = 0; i < links; i++)
            LinkOrSkip(Path.Combine(cwd, $"l{i}"), i == links - 1 ? "end.json" : $"l{i + 1}");

        LinkOrSkip(Mcp, "l0");

        ProcessSpec spec = Build();

        AssertRefused(spec);
        Assert.Contains("too long", spec.Warnings![0], StringComparison.Ordinal);
        Assert.False(File.Exists(Copy));
    }

    // ---- user_mcp ----------------------------------------------------------------------------------------------------

    [Fact]
    public void UserMcpDropsStrictMcpConfigAndNamesTheUserServers()
    {
        ProcessSpec spec = Build(userMcp: true);

        Assert.DoesNotContain("--strict-mcp-config", spec.Args);
        Assert.DoesNotContain("--mcp-config", spec.Args);
        Assert.Equal(["user"], ServersOf(spec));
        Assert.Null(spec.Warnings);
    }

    // The opt-in replaces the repo-file handling, it does not add to it: nothing is copied, judged or warned about.
    [Fact]
    public void UserMcpLeavesTheReposMcpJsonUntouchedEvenWhenItIsBroken()
    {
        File.WriteAllText(Mcp, "not json");

        ProcessSpec spec = Build(userMcp: true);

        Assert.DoesNotContain("--mcp-config", spec.Args);
        Assert.Equal(["user"], ServersOf(spec));
        Assert.Null(spec.Warnings);
        Assert.False(File.Exists(Copy));
    }

    // ---- the rest of the argv is the same either way -----------------------------------------------------------------

    [Fact]
    public void ThePermissionArgsAreTheSameWithAndWithoutAnMcpJson()
    {
        string[] without = Build().Args;
        File.WriteAllText(Mcp, Servers);
        string[] with = Build().Args;

        string[] withoutPair = [.. with.Where(arg => arg != Copy && arg != "--mcp-config")];
        Assert.Equal(without, withoutPair);
    }
}
