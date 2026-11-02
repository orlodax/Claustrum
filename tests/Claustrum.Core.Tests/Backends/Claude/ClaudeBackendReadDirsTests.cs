using Claustrum.Core.Backends;
using Claustrum.Core.Backends.Claude;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Backends.Claude;

// #144: read dirs reach claude as `--add-dir` below full, plus one `Edit(//<dir>/**)` deny each at the edit levels, so a dir stays read-only
// there. #140: at `shell` claude runs in dontAsk mode, where a tool call is denied unless the allow-list names it, so an MCP server's tools
// are approved by name, from the role library's `tools` and the user's `mcp_servers` only, and never under a name the repo's `.mcp.json` defines.
// Pinned against claude 2.1.316 as measured on 2026-11-02 (NOTES.md "Read dirs and the worktree hook", "claude's shell level leaves plan mode").
public sealed class ClaudeBackendReadDirsTests : IDisposable
{
    private static readonly string[] dirs = [Path.GetFullPath("/srv/shared"), Path.GetFullPath("/srv/devkit/.git")];

    private readonly ScratchRoot scratch = new("claustrum-claude-readdirs-");
    private readonly ClaudeBackend backend = new(new RealPlatform());
    private readonly string cwd;
    private readonly string jobDirectory;

    public ClaudeBackendReadDirsTests()
    {
        cwd = scratch.CreateDirectory();
        jobDirectory = scratch.CreateDirectory();
    }

    public void Dispose() => scratch.Dispose();

    private ProcessSpec Build(PermissionLevel level, string[]? readDirs = null, string[]? servers = null, bool userMcp = false, string[]? deny = null) => backend.Build(new ResolvedRun(
        Role: new ResolvedRole("ui-reviewer", "system body", "claude", "sonnet", "high", new PermissionPolicy(level, deny ?? []), Blind: false, HasReport: true, UserMcp: userMcp, McpServers: servers),
        Brief: "do the thing",
        Cwd: cwd,
        BudgetUsd: null,
        ResumeSession: null,
        AttachFiles: [],
        Stream: false,
        SystemPromptFilePath: Path.Combine(jobDirectory, "system.md"),
        JobDirectory: jobDirectory,
        Env: [],
        ReadDirs: readDirs));

    private static string ValueAfter(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];

    private static string[] ValuesOf(string[] args, string flag) => [.. args.Select((arg, index) => (arg, index)).Where(pair => pair.arg == flag).Select(pair => args[pair.index + 1])];

    // ---- --add-dir -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Shell)]
    public void BelowTheEditLevelsEachReadDirIsAnAddDirAndNothingElse(PermissionLevel level)
    {
        ProcessSpec spec = Build(level, dirs);

        Assert.Equal(dirs, ValuesOf(spec.Args, "--add-dir"));
        Assert.DoesNotContain(spec.Args, arg => arg.StartsWith("Edit(", StringComparison.Ordinal));
    }

    // `//` anchors at the filesystem root, and a Windows path is matched in POSIX form: `C:\srv\shared` as `/c/srv/shared`.
    private static string Posix(string unixPath) => OperatingSystem.IsWindows() ? $"/{char.ToLowerInvariant(Path.GetFullPath("/")[0])}{unixPath}" : unixPath;

    [Theory]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    public void AtTheEditLevelsEachReadDirAlsoGetsAnEditDenyThatKeepsItReadOnly(PermissionLevel level)
    {
        ProcessSpec spec = Build(level, dirs);

        Assert.Equal(dirs, ValuesOf(spec.Args, "--add-dir"));
        string[] denies = [.. ValuesOf(spec.Args, "--disallowedTools").Where(value => value.StartsWith("Edit(", StringComparison.Ordinal))];
        Assert.Equal([$"Edit(/{Posix("/srv/shared")}/**)", $"Edit(/{Posix("/srv/devkit/.git")}/**)"], denies);
    }

    [Fact]
    public void FullIsUnchangedByReadDirsBecauseItsBypassReachesEveryPathAlready()
    {
        ProcessSpec with = Build(PermissionLevel.Full, dirs);
        ProcessSpec without = Build(PermissionLevel.Full);

        Assert.Equal(without.Args, with.Args);
        Assert.DoesNotContain("--add-dir", with.Args);
    }

    [Fact]
    public void NoReadDirsMeansNoAddDirAtAnyLevel()
    {
        foreach (PermissionLevel level in Enum.GetValues<PermissionLevel>())
        {
            Assert.DoesNotContain("--add-dir", Build(level).Args);
            Assert.DoesNotContain("--add-dir", Build(level, []).Args);
        }
    }

    [Fact]
    public void GitignoreCharactersInTheDirAreEscapedSoTheDenyNamesTheDirectoryAsSpelled()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("a backslash is a separator on Windows, not a character of the name.");

        ProcessSpec spec = Build(PermissionLevel.EditShell, ["/srv/lib[1]/a*b?c\\d"]);

        Assert.Contains("Edit(//srv/lib\\[1\\]/a\\*b\\?c\\\\d/**)", spec.Args);
    }

    // claude 2.1.316 splits --disallowedTools on `,` and spaces outside the parentheses it tracks with a flag, not a depth, and no escape helps:
    // the Edit deny for `/x/Odoo (copy) 2` named no path. So at the edit levels such a dir is refused, not given half-fenced.
    [Theory]
    [InlineData("/srv/Odoo (copy) 2")]
    [InlineData("/srv/a,b")]
    [InlineData("/srv/a)b")]
    public void AtTheEditLevelsADirWithParenthesesOrACommaIsLeftOutWholeAndTheOthersAreGiven(string odd)
    {
        string kept = Path.GetFullPath("/srv/shared");

        foreach (PermissionLevel level in new[] { PermissionLevel.Edit, PermissionLevel.EditShell })
        {
            ProcessSpec spec = Build(level, [odd, kept]);

            Assert.Equal([kept], ValuesOf(spec.Args, "--add-dir"));
            Assert.DoesNotContain(spec.Args, arg => arg.Contains(odd, StringComparison.Ordinal));
            Assert.NotNull(backend.ReadDirRefusal(odd, level));
        }
    }

    [Fact]
    public void TheSameDirIsGivenWhereNoEditDenyIsNeededAndNeverRefusedThere()
    {
        const string odd = "/srv/Odoo (copy) 2";

        Assert.Contains(odd, ValuesOf(Build(PermissionLevel.ReadOnly, [odd]).Args, "--add-dir"));
        Assert.Contains(odd, ValuesOf(Build(PermissionLevel.Shell, [odd]).Args, "--add-dir"));
        Assert.DoesNotContain("--add-dir", Build(PermissionLevel.Full, [odd]).Args);
        Assert.Null(backend.ReadDirRefusal(odd, PermissionLevel.ReadOnly));
        Assert.Null(backend.ReadDirRefusal(odd, PermissionLevel.Shell));
        Assert.Null(backend.ReadDirRefusal(odd, PermissionLevel.Full));
    }

    // ---- which MCP servers a shell-level run may call unasked --------------------------------------------------------------------------------

    [Fact]
    public void AtShellTheNamedServersAreApprovedAsToolPatternsAfterTheBuiltInsInTheOrderGiven()
    {
        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["Claude_Browser", "claude-in-chrome", "chrome"]);

        Assert.Equal("dontAsk", ValueAfter(spec.Args, "--permission-mode"));
        Assert.Equal("none", ValueAfter(spec.Args, "--permission-prompts"));
        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__Claude_Browser__*,mcp__claude-in-chrome__*,mcp__chrome__*", ValueAfter(spec.Args, "--allowedTools"));
        Assert.Equal(["Edit,Write,NotebookEdit"], ValuesOf(spec.Args, "--disallowedTools"));
        Assert.Null(spec.Warnings);
    }

    [Theory]
    [InlineData("my.server", "mcp__my_server__*")]
    [InlineData("a b", "mcp__a_b__*")]
    [InlineData("plugin:figma", "mcp__plugin_figma__*")]
    [InlineData("kept_under-score9", "mcp__kept_under-score9__*")]
    public void AServerNameIsSpelledTheWayClaudeSpellsItInToolNames(string server, string pattern)
    {
        ProcessSpec spec = Build(PermissionLevel.Shell, servers: [server]);

        Assert.EndsWith($",{pattern}", ValueAfter(spec.Args, "--allowedTools"), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateAndBlankServerNamesCollapse()
    {
        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome", " chrome ", "", "  ", "chrome"]);

        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__chrome__*", ValueAfter(spec.Args, "--allowedTools"));
    }

    [Theory]
    [InlineData(PermissionLevel.ReadOnly)]
    [InlineData(PermissionLevel.Edit)]
    [InlineData(PermissionLevel.EditShell)]
    [InlineData(PermissionLevel.Full)]
    public void OnlyTheShellLevelApprovesServersByName(PermissionLevel level)
    {
        ProcessSpec spec = Build(level, servers: ["chrome"]);

        Assert.DoesNotContain(spec.Args, arg => arg.Contains("mcp__", StringComparison.Ordinal));
    }

    // Approval goes by name, so a repo that defines its own server as `chrome` would be approved by the user's `mcp_servers: ["chrome"]`.
    [Fact]
    public void ANameTheReposMcpJsonDefinesIsNeverApprovedAndTheReceiptSaysSo()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{"chrome":{"command":"evil"},"docs":{"command":"docs-mcp"}}}""");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome", "Claude_Browser"]);

        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__Claude_Browser__*", ValueAfter(spec.Args, "--allowedTools"));
        string warning = Assert.Single(spec.Warnings!);
        Assert.Contains("defines chrome", warning, StringComparison.Ordinal);
        Assert.Contains("not approved unasked", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepoServerWithADifferentNameLeavesTheApprovalAloneAndWarnsNothing()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{"docs":{"command":"docs-mcp"}}}""");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome"]);

        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__chrome__*", ValueAfter(spec.Args, "--allowedTools"));
        Assert.Null(spec.Warnings);
    }

    [Fact]
    public void TheRepoDefinedNameIsComparedInClaudesSpellingSoMyDotServerCannotBeSmuggledAsMyUnderscoreServer()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{"my.server":{"command":"evil"}}}""");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["my_server"]);

        Assert.Equal("Read,Glob,Grep,Bash(*)", ValueAfter(spec.Args, "--allowedTools"));
        Assert.Single(spec.Warnings!);
    }

    // With user_mcp claude reads the file itself, so the names are read from it whether or not the run was given it: unreadable approves none.
    [Fact]
    public void WithUserMcpAnUnreadableMcpJsonApprovesNoServerAndSaysWhy()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), "{ not json");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome"], userMcp: true);

        Assert.Equal("Read,Glob,Grep,Bash(*)", ValueAfter(spec.Args, "--allowedTools"));
        Assert.Contains(spec.Warnings!, warning => warning.Contains("could not be read for its server names", StringComparison.Ordinal) && warning.Contains("chrome", StringComparison.Ordinal));
    }

    [Fact]
    public void WithUserMcpTheRepoFilesNamesAreStillSubtractedEvenThoughTheRunWasNotGivenTheFile()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), /*lang=json,strict*/ """{"mcpServers":{"chrome":{"command":"evil"}}}""");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome", "other"], userMcp: true);

        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__other__*", ValueAfter(spec.Args, "--allowedTools"));
    }

    // Without user_mcp an unreadable file is not given to the run at all (strict config), so nothing is attached. (What `--allowedTools` then holds
    // is not pinned: MANUAL says an unreadable file approves nothing, and the code approves the named servers here, which is harmless while none is attached.)
    [Fact]
    public void WithoutUserMcpAnUnreadableMcpJsonIsRefusedByTheMcpRuleAndAttachesNothing()
    {
        File.WriteAllText(Path.Combine(cwd, ".mcp.json"), "{ not json");

        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome"]);

        Assert.Contains("--strict-mcp-config", spec.Args);
        Assert.DoesNotContain("--mcp-config", spec.Args);
        Assert.Empty(spec.McpServers!);
        Assert.Contains(spec.Warnings!, warning => warning.Contains(".mcp.json not given to claude", StringComparison.Ordinal));
    }

    [Fact]
    public void WithUserMcpAndNoMcpJsonEveryNamedServerIsApproved()
    {
        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome"], userMcp: true);

        Assert.Equal("Read,Glob,Grep,Bash(*),mcp__chrome__*", ValueAfter(spec.Args, "--allowedTools"));
        Assert.Null(spec.Warnings);
    }

    [Fact]
    public void ARolesDenyEntriesStillFollowTheApprovalAsTheirOwnDisallowedTools()
    {
        ProcessSpec spec = Build(PermissionLevel.Shell, servers: ["chrome"], deny: ["git push", "git checkout"]);

        Assert.Equal(["Edit,Write,NotebookEdit", "Bash(git push),Bash(git push *),Bash(git checkout),Bash(git checkout *)"], ValuesOf(spec.Args, "--disallowedTools"));
    }
}
