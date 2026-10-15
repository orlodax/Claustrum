using Claustrum.Cli;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Cli;

// #98 through the built binary: `init` fences its own writes to the cwd. `.claustrum/{casts,briefs,worktrees}` (all three or
// none) and `claustrum.json`, `.gitignore`, `AGENTS.md` (the full path, leaf link included, because the writes follow a leaf
// link) are checked first; one that resolves outside the repo prints `<name>: refused (resolves outside the repo → <real>)`, is
// neither read nor written, nothing is created at its target, and init exits 2 at the end with the harness syncs still run.
// A link that stays inside is followed as before. Paths are matched by their tails and stdout has `\r\n` folded to `\n`.
public sealed class InitLinkedPathEndToEndTests : IDisposable
{
    private const string Prefix = "refused (resolves outside the repo → ";

    private static readonly StringComparison pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ClaustrumCli cli = ClaustrumCli.Initialized();
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-init-link-e2e-").FullName;
    private readonly string outside;

    public InitLinkedPathEndToEndTests()
    {
        outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine\n");
    }

    public void Dispose()
    {
        cli.Dispose();
        TempTree.Delete(scratch);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] Lines(CliResult result) => Lf(result.Stdout).Split('\n');

    private static string[] Snapshot(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    private string InRepo(params string[] parts) => Path.Combine([cli.Cwd, .. parts]);

    private string Outside(string name, string? content)
    {
        string path = Path.Combine(outside, name);
        if (content is not null)
            File.WriteAllText(path, content);

        return path;
    }

    private Task<CliResult> InitAsync() => cli.RunAsync(["init"], "", null);

    // The one `<name>: refused (…)` line, reduced to the real path it prints.
    private static string RefusedLine(CliResult result, string name)
    {
        string line = Assert.Single(Lines(result), candidate => candidate.StartsWith($"{name}: ", StringComparison.Ordinal));
        Assert.StartsWith($"{name}: {Prefix}", line, StringComparison.Ordinal);
        Assert.EndsWith(")", line, StringComparison.Ordinal);
        return line[$"{name}: {Prefix}".Length..^1];
    }

    private static void AssertNoCrash(CliResult result)
    {
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Stderr, StringComparison.Ordinal);
    }

    // Bytes unchanged when the target was there; still absent when it dangled (writing through the link would have created it).
    private static void AssertTargetUntouched(string target, byte[]? before)
    {
        if (before is not null)
            Assert.Equal(before, File.ReadAllBytes(target));
        else
            Assert.False(File.Exists(target), "init created the dangling target");
    }

    // The harness syncs are separate from the fenced writes: a refusal here does not stop them, and the exit code says it at the end.
    private void AssertHarnessSyncStillRan(CliResult result)
    {
        Assert.Contains(Lines(result), line => line.StartsWith("claude: ", StringComparison.Ordinal));
        Assert.True(File.Exists(InRepo(".claude", "agents", "builder.md")));
    }

    [Fact]
    public async Task ALinkedClaustrumDirectoryOutsideTheRepoIsRefusedAndNothingIsCreatedThereAsync()
    {
        TestLinks.CreateDirectoryLink(InRepo(".claustrum"), outside);
        string[] before = Snapshot(outside);

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.StartsWith(outside, RefusedLine(result, ".claustrum/"), pathComparison);
        Assert.Equal(before, Snapshot(outside));
        Assert.NotNull(new DirectoryInfo(InRepo(".claustrum")).LinkTarget);
        AssertHarnessSyncStillRan(result);
        AssertNoCrash(result);
    }

    // All three directories or none: casts and briefs are in the repo, worktrees leads out, so none is created.
    [Fact]
    public async Task OneScaffoldDirectoryLinkedOutsideStopsTheOtherTwoFromBeingCreatedAsync()
    {
        string target = Directory.CreateDirectory(Path.Combine(outside, "elsewhere")).FullName;
        Directory.CreateDirectory(InRepo(".claustrum"));
        TestLinks.CreateDirectoryLink(InRepo(".claustrum", "worktrees"), target);
        string[] before = Snapshot(outside);

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(target, RefusedLine(result, ".claustrum/"), pathComparer);
        Assert.False(Directory.Exists(InRepo(".claustrum", "casts")));
        Assert.False(Directory.Exists(InRepo(".claustrum", "briefs")));
        Assert.Equal(before, Snapshot(outside));
        AssertNoCrash(result);
    }

    // Existing target: neither read nor rewritten. Dangling target: appending through the link would create it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALinkedAgentsMdOutsideTheRepoIsRefusedAndNothingIsWrittenThroughItAsync(bool targetExists)
    {
        string notes = Outside("notes.md", targetExists ? "mine" : null);
        byte[]? before = targetExists ? File.ReadAllBytes(notes) : null;
        TestLinks.Create(InRepo("AGENTS.md"), notes);
        string[] snapshot = Snapshot(outside);

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(notes, RefusedLine(result, "AGENTS.md"), pathComparer);
        Assert.Equal(snapshot, Snapshot(outside));
        AssertTargetUntouched(notes, before);
        AssertHarnessSyncStillRan(result);
        AssertNoCrash(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALinkedGitignoreOutsideTheRepoIsRefusedAndNothingIsWrittenThroughItAsync(bool targetExists)
    {
        string ignore = Outside("ignore", targetExists ? "node_modules/\n" : null);
        byte[]? before = targetExists ? File.ReadAllBytes(ignore) : null;
        TestLinks.Create(InRepo(".gitignore"), ignore);
        string[] snapshot = Snapshot(outside);

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(ignore, RefusedLine(result, ".gitignore"), pathComparer);
        Assert.Equal(snapshot, Snapshot(outside));
        AssertTargetUntouched(ignore, before);
        AssertHarnessSyncStillRan(result);
        AssertNoCrash(result);
    }

    // Existing target: neither read for its content nor rewritten. Dangling target: writing through the link would create it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALinkedClaustrumJsonOutsideTheRepoIsRefusedWhetherItsTargetExistsOrDanglesAsync(bool targetExists)
    {
        string config = Outside("cfg.json", targetExists ? /*lang=json,strict*/ """{"mine":true}""" : null);
        byte[]? before = targetExists ? File.ReadAllBytes(config) : null;
        TestLinks.Create(InRepo("claustrum.json"), config);
        string[] snapshot = Snapshot(outside);

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(config, RefusedLine(result, "claustrum.json"), pathComparer);
        Assert.Equal(snapshot, Snapshot(outside));
        AssertTargetUntouched(config, before);
        AssertHarnessSyncStillRan(result);
        AssertNoCrash(result);
    }

    [Fact]
    public async Task EveryFencedPathLinkedOutsideIsRefusedOnItsOwnLineAndNothingChangesOutsideAsync()
    {
        TestLinks.CreateDirectoryLink(InRepo(".claustrum"), outside);
        TestLinks.Create(InRepo("claustrum.json"), Outside("cfg.json", null));
        TestLinks.Create(InRepo(".gitignore"), Outside("ignore", "node_modules/\n"));
        TestLinks.Create(InRepo("AGENTS.md"), Outside("notes.md", "mine"));
        string[] before = Snapshot(outside);
        byte[] ignore = File.ReadAllBytes(Path.Combine(outside, "ignore"));
        byte[] notes = File.ReadAllBytes(Path.Combine(outside, "notes.md"));

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        foreach (string name in new[] { ".claustrum/", "claustrum.json", ".gitignore", "AGENTS.md" })
            Assert.False(string.IsNullOrEmpty(RefusedLine(result, name)));

        Assert.Equal(before, Snapshot(outside));
        Assert.Equal(ignore, File.ReadAllBytes(Path.Combine(outside, "ignore")));
        Assert.Equal(notes, File.ReadAllBytes(Path.Combine(outside, "notes.md")));
        AssertHarnessSyncStillRan(result);
        AssertNoCrash(result);
    }

    [Fact]
    public async Task AnAgentsMdLinkThatStaysInsideTheRepoGetsItsPointerWrittenThroughItAsync()
    {
        Directory.CreateDirectory(InRepo("docs"));
        File.WriteAllText(InRepo("docs", "AGENTS.md"), "# House rules\n");
        TestLinks.Create(InRepo("AGENTS.md"), Path.Combine("docs", "AGENTS.md"));

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        Assert.Contains("AGENTS.md: pointer appended", Lines(result));
        string real = File.ReadAllText(InRepo("docs", "AGENTS.md"));
        Assert.StartsWith("# House rules", real, StringComparison.Ordinal);
        Assert.Contains("## Claustrum delegation", real, StringComparison.Ordinal);
        Assert.NotNull(new FileInfo(InRepo("AGENTS.md")).LinkTarget);
        Assert.DoesNotContain("refused", Lf(result.Stdout), StringComparison.Ordinal);
        AssertNoCrash(result);
    }

    // The fence refuses what leaves the repo, not what is linked: a dotfiles-style `.claustrum` that stays inside is followed.
    [Fact]
    public async Task AClaustrumDirectoryLinkedInsideTheRepoGetsItsScaffoldCreatedThroughTheLinkAsync()
    {
        string store = Directory.CreateDirectory(InRepo("dotfiles", "claustrum")).FullName;
        TestLinks.CreateDirectoryLink(InRepo(".claustrum"), Path.Combine("dotfiles", "claustrum"));

        CliResult result = await InitAsync();

        Assert.Equal(ExitCodes.Ok, result.ExitCode);
        foreach (string name in new[] { "casts", "briefs", "worktrees" })
            Assert.True(Directory.Exists(Path.Combine(store, name)), $"{name} was not created through the link");

        Assert.DoesNotContain("refused", Lf(result.Stdout), StringComparison.Ordinal);
        AssertNoCrash(result);
    }

    // The status lines of a repo with nothing linked keep their wording, so a script reading them is not broken by the fence.
    [Fact]
    public async Task APlainRepoPrintsTheSameStatusLinesAsBeforeAndRefusesNothingAsync()
    {
        File.WriteAllText(InRepo("AGENTS.md"), "# House rules\n");

        CliResult first = await InitAsync();
        CliResult second = await InitAsync();

        Assert.Equal(ExitCodes.Ok, first.ExitCode);
        Assert.Contains(Lines(first), line => line.StartsWith("claustrum.json: not written (aliases are per-developer, in ", StringComparison.Ordinal));
        Assert.False(File.Exists(InRepo("claustrum.json")), "init writes no repo claustrum.json (#122)");
        Assert.Contains(".gitignore: updated", Lines(first));
        Assert.Contains("AGENTS.md: pointer appended", Lines(first));
        Assert.Equal(ExitCodes.Ok, second.ExitCode);
        Assert.Contains(Lines(second), line => line.StartsWith("claustrum.json: not written (aliases are per-developer, in ", StringComparison.Ordinal));
        Assert.Contains(".gitignore: already up to date", Lines(second));
        Assert.Contains("AGENTS.md: unchanged", Lines(second));
        Assert.DoesNotContain("refused", Lf(first.Stdout) + Lf(second.Stdout), StringComparison.Ordinal);
    }
}
