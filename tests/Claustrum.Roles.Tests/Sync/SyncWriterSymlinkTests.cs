using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #84: a symlink at a path sync would write is never sync's own file (sync never creates one). Without --force it is
// foreign, whatever its target holds; with --force it is replaced by a regular file and the target is left byte-identical.
// Regular files are still written in place (NOTES.md "`sync` never writes through a symlinked file"). Links are made with
// File.CreateSymbolicLink and a case skips, with the reason, only when the machine will not make one (Windows without
// Developer Mode or elevation).
public sealed class SyncWriterSymlinkTests : IDisposable
{
    private const string Frontmatter = "---\nname: builder\n---";
    private const string Body = "Generated body.";

    private readonly string root = Directory.CreateTempSubdirectory("claustrum-symlink-").FullName;
    private readonly string agents;
    private readonly string elsewhere;
    private readonly SyncWriter writer = new("claude", "1.0.0");

    public SyncWriterSymlinkTests()
    {
        agents = Directory.CreateDirectory(Path.Combine(root, "repo", ".claude", "agents")).FullName;
        elsewhere = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(root, recursive: true);
    }

    private string LinkPath => Path.Combine(agents, "builder.md");

    private static void CreateLink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"symlinks cannot be created here: {ex.Message}");
        }
    }

    private SyncAccumulator Write(string path, bool force, SyncMode mode = SyncMode.Write)
    {
        SyncAccumulator into = new();
        writer.Write(path, "builder", Frontmatter, Body, force, mode, into, SyncRoot.Global(root));
        return into;
    }

    /// <summary>What the writer produces for this role and body, taken from a real write to a scratch path.</summary>
    private string GeneratedContent()
    {
        string scratch = Path.Combine(elsewhere, "generated-reference.md");
        Write(scratch, force: false);
        string content = File.ReadAllText(scratch);
        File.Delete(scratch);
        return content;
    }

    private string TargetWith(string content)
    {
        string target = Path.Combine(elsewhere, "target.md");
        File.WriteAllText(target, content);
        return target;
    }

    private static bool IsLinkTo(string path, string target) => new FileInfo(path).LinkTarget == target;

    private string[] LeftoverTempFiles() => [.. Directory.GetFiles(agents, "*.claustrum-tmp")];

    public static TheoryData<SyncMode> Modes => [SyncMode.Write, SyncMode.DryRun, SyncMode.Check];

    public static TheoryData<SyncMode> ModesThatDoNotWrite => [SyncMode.DryRun, SyncMode.Check];

    [Theory]
    [MemberData(nameof(Modes))]
    public void AValidLinkWithoutForceIsForeignAndItsTargetIsNeverTouched(SyncMode mode)
    {
        string target = TargetWith("my own notes, no marker\n");
        CreateLink(LinkPath, target);
        byte[] before = File.ReadAllBytes(target);

        SyncAccumulator into = Write(LinkPath, force: false, mode);

        Assert.Equal([LinkPath], into.Foreign);
        Assert.Equal(target, into.Symlinks[LinkPath]);
        Assert.Empty(into.Written);
        Assert.Empty(into.Skipped);
        Assert.Empty(into.ManifestFiles);
        Assert.Empty(into.ProposedContent);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.True(IsLinkTo(LinkPath, target));
        Assert.Empty(LeftoverTempFiles());
    }

    // Reading through the link would call this "up to date" and skip it; a link is never sync's own file, so it is foreign on every run.
    [Theory]
    [MemberData(nameof(Modes))]
    public void ALinkWhoseTargetIsByteIdenticalGeneratedContentIsStillForeignNeverSkipped(SyncMode mode)
    {
        string target = TargetWith(GeneratedContent());
        CreateLink(LinkPath, target);

        SyncAccumulator into = Write(LinkPath, force: false, mode);

        Assert.Equal([LinkPath], into.Foreign);
        Assert.Empty(into.Skipped);
        Assert.Empty(into.Written);
        Assert.True(IsLinkTo(LinkPath, target));
    }

    // A marked regular file is rewritten freely; the same bytes behind a link are not sync's to rewrite.
    [Theory]
    [MemberData(nameof(Modes))]
    public void ALinkToAFileCarryingOurMarkerIsForeignWithoutForceAndNotRewrittenThroughTheLink(SyncMode mode)
    {
        string stale = GeneratedContent().Replace("Generated body.", "An older body.", StringComparison.Ordinal);
        string target = TargetWith(stale);
        CreateLink(LinkPath, target);

        SyncAccumulator into = Write(LinkPath, force: false, mode);

        Assert.Equal([LinkPath], into.Foreign);
        Assert.Empty(into.Written);
        Assert.Equal(stale, File.ReadAllText(target));
        Assert.True(IsLinkTo(LinkPath, target));
    }

    [Theory]
    [MemberData(nameof(ModesThatDoNotWrite))]
    public void ForceOverAValidLinkProposesTheReplacementWithoutTouchingDiskInDryRunAndCheck(SyncMode mode)
    {
        string target = TargetWith("my own notes, no marker\n");
        CreateLink(LinkPath, target);
        byte[] before = File.ReadAllBytes(target);
        string expected = GeneratedContent();

        SyncAccumulator into = Write(LinkPath, force: true, mode);

        Assert.Equal([LinkPath], into.Written);
        Assert.Empty(into.Foreign);
        Assert.Equal(expected, into.ProposedContent[LinkPath]);
        Assert.Equal(target, into.Symlinks[LinkPath]);
        Assert.True(IsLinkTo(LinkPath, target));
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public void ForceOverAValidLinkReplacesItWithARegularMarkedFileAndLeavesTheTargetByteIdentical()
    {
        string target = TargetWith("my own notes, no marker\n");
        CreateLink(LinkPath, target);
        byte[] before = File.ReadAllBytes(target);
        string expected = GeneratedContent();

        SyncAccumulator into = Write(LinkPath, force: true);

        Assert.Equal([LinkPath], into.Written);
        Assert.Equal(target, into.Symlinks[LinkPath]);
        Assert.Equal([LinkPath], into.ManifestFiles.Select(file => file.Path));
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Equal(expected, File.ReadAllText(LinkPath));
        Assert.Contains(SyncWriter.MarkerPrefix, File.ReadAllText(LinkPath), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Empty(LeftoverTempFiles());
        Assert.Equal([LinkPath], Directory.GetFileSystemEntries(agents));
    }

    [Fact]
    public void ARelativeLinkIsReportedWithItsTargetAsWrittenAndReplacedTheSameWay()
    {
        string target = TargetWith("my own notes, no marker\n");
        string relative = Path.GetRelativePath(agents, target);
        CreateLink(LinkPath, relative);

        SyncAccumulator foreign = Write(LinkPath, force: false);
        SyncAccumulator forced = Write(LinkPath, force: true);

        Assert.Equal(relative, foreign.Symlinks[LinkPath]);
        Assert.Equal([LinkPath], foreign.Foreign);
        Assert.Equal([LinkPath], forced.Written);
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Equal("my own notes, no marker\n", File.ReadAllText(target));
    }

    // File.Exists is true for a dangling link on Unix and reading through one throws: the link has to be recognised first.
    [Theory]
    [MemberData(nameof(Modes))]
    public void ADanglingLinkWithoutForceIsForeignInEveryModeAndNothingIsCreatedAtItsTarget(SyncMode mode)
    {
        string missing = Path.Combine(elsewhere, "missing.md");
        CreateLink(LinkPath, missing);

        SyncAccumulator into = Write(LinkPath, force: false, mode);

        Assert.Equal([LinkPath], into.Foreign);
        Assert.Equal(missing, into.Symlinks[LinkPath]);
        Assert.Empty(into.Written);
        Assert.False(File.Exists(missing));
        Assert.True(IsLinkTo(LinkPath, missing));
    }

    [Theory]
    [MemberData(nameof(ModesThatDoNotWrite))]
    public void ForceOverADanglingLinkProposesTheReplacementInDryRunAndCheck(SyncMode mode)
    {
        string missing = Path.Combine(elsewhere, "missing.md");
        CreateLink(LinkPath, missing);

        SyncAccumulator into = Write(LinkPath, force: true, mode);

        Assert.Equal([LinkPath], into.Written);
        Assert.Equal(GeneratedContent(), into.ProposedContent[LinkPath]);
        Assert.Equal(missing, into.Symlinks[LinkPath]);
        Assert.True(IsLinkTo(LinkPath, missing));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void ForceOverADanglingLinkWritesARegularFileAndStillCreatesNothingAtTheTarget()
    {
        string missing = Path.Combine(elsewhere, "missing.md");
        CreateLink(LinkPath, missing);

        SyncAccumulator into = Write(LinkPath, force: true);

        Assert.Equal([LinkPath], into.Written);
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Contains(SyncWriter.MarkerPrefix, File.ReadAllText(LinkPath), StringComparison.Ordinal);
        Assert.False(File.Exists(missing));
        Assert.Empty(LeftoverTempFiles());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ALinkToItselfIsForeignWithoutForceInEveryModeAndDoesNotThrow(SyncMode mode)
    {
        CreateLink(LinkPath, LinkPath);

        SyncAccumulator into = Write(LinkPath, force: false, mode);

        Assert.Equal([LinkPath], into.Foreign);
        Assert.Equal(LinkPath, into.Symlinks[LinkPath]);
        Assert.Empty(into.Written);
    }

    [Fact]
    public void ForceReplacesALinkToItselfWithARegularFile()
    {
        CreateLink(LinkPath, LinkPath);

        SyncAccumulator into = Write(LinkPath, force: true);

        Assert.Equal([LinkPath], into.Written);
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Contains(SyncWriter.MarkerPrefix, File.ReadAllText(LinkPath), StringComparison.Ordinal);
    }

    // A link whose target is a directory is still a link: foreign without --force, replaced with it, the directory left whole.
    // Windows makes directory links with a different reparse kind and nothing here has measured renaming a file over one.
    [Fact]
    public void ALinkToADirectoryIsForeignWithoutForceAndReplacedWithItLeavingTheDirectoryIntact()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("a Windows directory link is a different reparse kind, and renaming a file over one has not been measured.");
            return;
        }

        string directory = Directory.CreateDirectory(Path.Combine(elsewhere, "somedir")).FullName;
        File.WriteAllText(Path.Combine(directory, "inside.txt"), "kept\n");
        Directory.CreateSymbolicLink(LinkPath, directory);

        SyncAccumulator plain = Write(LinkPath, force: false);
        SyncAccumulator forced = Write(LinkPath, force: true);

        Assert.Equal([LinkPath], plain.Foreign);
        Assert.Equal(directory, plain.Symlinks[LinkPath]);
        Assert.Equal([LinkPath], forced.Written);
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Contains(SyncWriter.MarkerPrefix, File.ReadAllText(LinkPath), StringComparison.Ordinal);
        Assert.Equal("kept\n", File.ReadAllText(Path.Combine(directory, "inside.txt")));
        Assert.Empty(LeftoverTempFiles());
    }

    // The temp name for a replaced link is random and opened exclusively, so a link planted at the guessable name
    // `.builder.md.claustrum-tmp` (a repo can commit one) is neither followed nor removed (found 2026-10-12).
    [Fact]
    public void ALinkPlantedAtThePredictableTempNameIsLeftAloneByAPlainWriteOfARegularFile()
    {
        string victim = Path.Combine(elsewhere, "victim.txt");
        File.WriteAllText(victim, "do not touch\n");
        string planted = Path.Combine(agents, ".builder.md.claustrum-tmp");
        CreateLink(planted, victim);

        SyncAccumulator into = Write(LinkPath, force: false);

        Assert.Equal([LinkPath], into.Written);
        Assert.Equal("do not touch\n", File.ReadAllText(victim));
        Assert.True(IsLinkTo(planted, victim));
    }

    [Fact]
    public void ALinkPlantedAtThePredictableTempNameSurvivesReplacingAnotherLinkAndItsTargetStaysIntact()
    {
        string victim = Path.Combine(elsewhere, "victim.txt");
        File.WriteAllText(victim, "do not touch\n");
        string planted = Path.Combine(agents, ".builder.md.claustrum-tmp");
        CreateLink(planted, victim);
        string target = TargetWith("my own notes, no marker\n");
        CreateLink(LinkPath, target);

        SyncAccumulator into = Write(LinkPath, force: true);

        Assert.Equal([LinkPath], into.Written);
        Assert.Null(new FileInfo(LinkPath).LinkTarget);
        Assert.Equal("do not touch\n", File.ReadAllText(victim));
        Assert.Equal("my own notes, no marker\n", File.ReadAllText(target));
        Assert.True(IsLinkTo(planted, victim));
        Assert.Equal([planted], LeftoverTempFiles());
    }

    // A regular marked file is written in place: it keeps its mode, which a rename of a fresh file would have reset to 0644.
    [Fact]
    public void AnInPlaceWriteKeepsTheModeOfTheFileItRewrites()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX file modes do not exist on Windows.");
            return;
        }

        string stale = GeneratedContent().Replace("Generated body.", "An older body.", StringComparison.Ordinal);
        File.WriteAllText(LinkPath, stale);
        File.SetUnixFileMode(LinkPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SyncAccumulator into = Write(LinkPath, force: false);

        Assert.Equal([LinkPath], into.Written);
        Assert.Equal(GeneratedContent(), File.ReadAllText(LinkPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LinkPath));
    }

    // Rename-over needs write access to the directory; an in-place write needs it to the file only (the regression the first #84 fix had).
    [Fact]
    public void AnInPlaceWriteWorksInADirectoryTheUserCannotWriteTo()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX directory permissions do not exist on Windows.");
            return;
        }

        string stale = GeneratedContent().Replace("Generated body.", "An older body.", StringComparison.Ordinal);
        File.WriteAllText(LinkPath, stale);
        File.SetUnixFileMode(agents, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        SyncAccumulator into = Write(LinkPath, force: false);

        Assert.Equal([LinkPath], into.Written);
        Assert.Equal(GeneratedContent(), File.ReadAllText(LinkPath));
    }

    // End to end through each harness's own sync: the link sits where the dotfiles manager put it, in the repo's agents dir.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void EachHarnessSyncTreatsALinkedAgentFileAsForeignAndForceReplacesItLeavingTheTarget(string harness)
    {
        string cwd = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        string repoRoot = HarnessSyncs.RepoRoot(harness, cwd);
        string agent = HarnessSyncs.AgentFile(harness, repoRoot, "builder");
        Directory.CreateDirectory(HarnessSyncs.AgentDirectory(harness, repoRoot));
        string target = TargetWith("my own notes, no marker\n");
        CreateLink(agent, target);
        RoleLibrary library = new();

        SyncResult plain = HarnessSyncs.Run(harness, library, home, cwd, ["builder"]);
        SyncResult forced = HarnessSyncs.Run(harness, library, home, cwd, ["builder"], force: true);

        Assert.Contains(agent, plain.Foreign);
        Assert.NotNull(plain.Symlinks);
        Assert.Equal(target, plain.Symlinks[agent]);
        Assert.DoesNotContain(agent, plain.Written);
        Assert.DoesNotContain(agent, plain.Skipped);
        Assert.Contains(agent, forced.Written);
        Assert.Null(new FileInfo(agent).LinkTarget);
        Assert.Equal("my own notes, no marker\n", File.ReadAllText(target));
    }
}
