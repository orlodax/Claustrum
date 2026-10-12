using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #84 through the built binary: a symlinked agent file is foreign (named with its target), `--force` replaces it and says so,
// and --dry-run/--check say "would replace symlink" only with --force. The link target is the user's file, possibly in another
// repository, and every case ends by proving it was not written through. Paths are matched by their repo-relative tail
// (the child may print the temp directory in another spelling on Windows) and stdout has `\r\n` folded to `\n`.
public sealed class SyncSymlinkEndToEndTests : IDisposable
{
    private static readonly string agentTail = Path.Combine(".claude", "agents", "builder.md");

    private readonly ClaustrumCli cli = new();
    private readonly string outside = Directory.CreateTempSubdirectory("claustrum-symlink-target-").FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string file in Directory.EnumerateFiles(outside))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        cli.Dispose();
        TempTree.Delete(outside);
    }

    private string Agent => Path.Combine(cli.Cwd, agentTail);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> SyncAsync(params string[] args) => cli.RunAsync(["sync", "--roles", "builder", .. args], "", null);

    /// <summary>A file the user owns, outside the repo, with the agent path linked to it.</summary>
    private string LinkAgentToFileWith(string content)
    {
        string target = Path.Combine(outside, "notes.md");
        File.WriteAllText(target, content);
        LinkAgentTo(target);
        return target;
    }

    private void LinkAgentTo(string target)
    {
        Directory.CreateDirectory(Path.Combine(cli.Cwd, ".claude", "agents"));
        TestLinks.Create(Agent, target);
    }

    private static string[] Lines(CliResult result) => Lf(result.Stdout).Split('\n');

    /// <summary>The indented lines under a section header such as `skipped (already up to date):`, empty when there is none.</summary>
    private static string[] Section(CliResult result, string header)
    {
        string[] lines = Lines(result);
        int start = Array.IndexOf(lines, header);
        return start < 0 ? [] : [.. lines[(start + 1)..].TakeWhile(line => line.StartsWith("  ", StringComparison.Ordinal))];
    }

    private static void AssertNoCrash(CliResult result)
    {
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainSyncNamesTheLinkAsForeignWithItsTargetAndLeavesBothAloneAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string foreign = Assert.Single(Section(result, "foreign (hand-edited; use --force to adopt):"));
        Assert.EndsWith($"{agentTail} (symlink → {target})", foreign, StringComparison.Ordinal);
        Assert.DoesNotContain("replaced symlink", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task ForceReplacesTheLinkNamesItUnderReplacedSymlinkAndLeavesTheTargetByteIdenticalAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync("--force");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string replaced = Assert.Single(Section(result, "replaced symlink (the link target was left untouched):"));
        Assert.EndsWith($"{agentTail} → {target}", replaced, StringComparison.Ordinal);
        Assert.DoesNotContain(Section(result, "written:"), line => line.EndsWith(agentTail, StringComparison.Ordinal));
        Assert.DoesNotContain("foreign", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("(symlink →", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Null(new FileInfo(Agent).LinkTarget);
        Assert.Contains("claustrum:generated", File.ReadAllText(Agent), StringComparison.Ordinal);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
        AssertNoCrash(result);

        // The second run finds an ordinary marked file: nothing to replace, nothing foreign.
        CliResult again = await SyncAsync();

        Assert.DoesNotContain("replaced symlink", Lf(again.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("foreign", Lf(again.Stdout), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunWithoutForceListsTheLinkAsForeignAndNeverProposesReplacingItAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync("--dry-run");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains($"{agentTail} (symlink → {target})", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("would replace symlink", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task DryRunWithForceSaysWouldReplaceSymlinkBeforeTheDiffAndTouchesNothingAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync("--dry-run", "--force");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        string[] lines = Lines(result);
        int would = Array.FindIndex(lines, line => line == $"would replace symlink {agentTail} → {target}");
        Assert.True(would >= 0, "no 'would replace symlink' line");
        Assert.Equal($"--- a/{agentTail}", lines[would + 1]);
        Assert.Contains("-my own notes", lines);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task CheckWithoutForceReportsTheLinkAsForeignAndExitsTwoAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync("--check");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains($"{agentTail} (symlink → {target})", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("would replace symlink", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
    }

    [Fact]
    public async Task CheckWithForceSaysWouldReplaceSymlinkAndStillExitsTwoWithoutWritingAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult result = await SyncAsync("--check", "--force");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        string line = Assert.Single(Lines(result), l => l.StartsWith("would replace symlink: ", StringComparison.Ordinal));
        Assert.EndsWith($"{agentTail} → {target}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("stale:", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
    }

    // A link whose target holds exactly what sync would generate is foreign on every run (never "up to date"), so
    // `--check` stays red until --force cuts the link (NOTES.md #84).
    [Fact]
    public async Task ALinkToByteIdenticalGeneratedContentIsStillForeignAndCheckExitsTwoAsync()
    {
        Assert.Equal(ExitCodes.Ok, (await SyncAsync()).ExitCode);
        string generated = Path.Combine(outside, "generated.md");
        File.Copy(Agent, generated);
        File.Delete(Agent);
        LinkAgentTo(generated);

        CliResult check = await SyncAsync("--check");
        CliResult plain = await SyncAsync();

        Assert.Equal(ExitCodes.Usage, check.ExitCode);
        Assert.Contains($"{agentTail} (symlink → {generated})", Lf(check.Stdout), StringComparison.Ordinal);
        Assert.DoesNotContain(Section(plain, "skipped (already up to date):"), line => line.EndsWith(agentTail, StringComparison.Ordinal));
        Assert.Contains($"{agentTail} (symlink → {generated})", Lf(plain.Stdout), StringComparison.Ordinal);
        Assert.Equal(generated, new FileInfo(Agent).LinkTarget);
    }

    // `init` runs the same sync with no --force, so a linked agent file is one more foreign file it counts and leaves alone.
    [Fact]
    public async Task InitCountsALinkedAgentAsForeignOnEveryRunAndNeverWritesThroughItAsync()
    {
        string target = LinkAgentToFileWith("my own notes\n");

        CliResult first = await cli.RunAsync(["init"], "", null);
        CliResult second = await cli.RunAsync(["init"], "", null);

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.Contains(", 1 foreign", Lf(first.Stdout), StringComparison.Ordinal);
        Assert.Contains(", 1 foreign", Lf(second.Stdout), StringComparison.Ordinal);
        Assert.Equal(target, new FileInfo(Agent).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
    }

    // The dotfiles-manager case that motivated both fixes: a relocated config directory holding a linked agent file.
    [Fact]
    public async Task GlobalSyncIntoARelocatedRootNamesTheLinkReplacesItOnlyWithForceAndLeavesTheTargetAsync()
    {
        string root = Path.Combine(outside, "claude-config");
        string agents = Directory.CreateDirectory(Path.Combine(root, "agents")).FullName;
        string target = Path.Combine(outside, "dotfile-builder.md");
        File.WriteAllText(target, "my own notes\n");
        string link = Path.Combine(agents, "builder.md");
        TestLinks.Create(link, target);
        Dictionary<string, string?> env = new() { ["CLAUDE_CONFIG_DIR"] = root };

        CliResult plain = await cli.RunAsync(["sync", "--global", "--only", "claude", "--roles", "builder"], "", null, env);
        CliResult forced = await cli.RunAsync(["sync", "--global", "--only", "claude", "--roles", "builder", "--force"], "", null, env);

        Assert.StartsWith($"global root (claude): {root} (CLAUDE_CONFIG_DIR)\n", Lf(plain.Stdout), StringComparison.Ordinal);
        Assert.Contains($"{link} (symlink → {target})", Lf(plain.Stdout), StringComparison.Ordinal);
        Assert.StartsWith($"global root (claude): {root} (CLAUDE_CONFIG_DIR)\n", Lf(forced.Stdout), StringComparison.Ordinal);
        Assert.Contains($"{link} → {target}", Lf(forced.Stdout), StringComparison.Ordinal);
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
    }

    public static TheoryData<string[], int, string> DanglingLinkCases => new()
    {
        { [], ExitCodes.Ok, "(symlink → " },
        { ["--dry-run"], ExitCodes.Ok, "(symlink → " },
        { ["--dry-run", "--force"], ExitCodes.Ok, "would replace symlink " },
        { ["--check"], ExitCodes.Usage, "(symlink → " },
        { ["--check", "--force"], ExitCodes.Usage, "would replace symlink: " },
        { ["--force"], ExitCodes.Ok, "replaced symlink (the link target was left untouched):" },
    };

    [Theory]
    [MemberData(nameof(DanglingLinkCases))]
    public async Task ADanglingLinkIsHandledInEveryModeWithoutACrashAndItsTargetIsNeverCreatedAsync(string[] args, int expectedExit, string expectedPhrase)
    {
        string missing = Path.Combine(outside, "not-there.md");
        LinkAgentTo(missing);

        CliResult result = await SyncAsync(args);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Contains(expectedPhrase, Lf(result.Stdout), StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
        AssertNoCrash(result);
    }

    [Fact]
    public async Task DryRunWithForceOverADanglingLinkDiffsAgainstNothingAsync()
    {
        string missing = Path.Combine(outside, "not-there.md");
        LinkAgentTo(missing);

        CliResult result = await SyncAsync("--dry-run", "--force");

        Assert.Contains($"would replace symlink {agentTail} → {missing}", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Contains("--- /dev/null", Lf(result.Stdout), StringComparison.Ordinal);
        Assert.Equal(missing, new FileInfo(Agent).LinkTarget);
    }

    public static TheoryData<string[], int> SelfReferencingLinkCases => new()
    {
        { [], ExitCodes.Ok },
        { ["--dry-run"], ExitCodes.Ok },
        { ["--dry-run", "--force"], ExitCodes.Ok },
        { ["--check"], ExitCodes.Usage },
        { ["--force"], ExitCodes.Ok },
    };

    // Reading through `a -> a` throws IOException ("Too many levels of symbolic links"); --dry-run must not die on it.
    [Theory]
    [MemberData(nameof(SelfReferencingLinkCases))]
    public async Task ALinkToItselfDoesNotCrashAnyModeAsync(string[] args, int expectedExit)
    {
        LinkAgentTo(Agent);

        CliResult result = await SyncAsync(args);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Contains(agentTail, Lf(result.Stdout), StringComparison.Ordinal);
        AssertNoCrash(result);
    }

    // A target the user cannot read (chmod 000) is the other way the diff's "before" can throw. Running as root reads it
    // anyway, so there the case passes without reaching the catch; it is exercised on any ordinary account.
    [Fact]
    public async Task DryRunWithForceOverALinkToAnUnreadableFileDoesNotCrashAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX file modes do not exist on Windows.");
            return;
        }

        string target = LinkAgentToFileWith("private notes\n");
        File.SetUnixFileMode(target, UnixFileMode.None);

        CliResult result = await SyncAsync("--dry-run", "--force");

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains($"would replace symlink {agentTail} → {target}", Lf(result.Stdout), StringComparison.Ordinal);
        AssertNoCrash(result);
    }
}
