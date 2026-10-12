using System.Text.Json.Nodes;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #95, #93: in repo mode a target whose bytes would land outside the real path of the cwd (a linked directory anywhere on
// the way; for JSON targets and the manifest a leaf link too) is Refused, in every mode, --force or not, and nothing is
// created outside. A link that stays inside the cwd is written through and annotated in RealPaths; under --global nothing
// is refused. A leaf link on an agent file keeps #84's meaning (Foreign, or replaced with --force) wherever it points.
public sealed class SyncLinkedDirectoryTests : IDisposable
{
    private const string OverrideText = "ZZ-LOCAL-OVERRIDE-TEXT";

    private static readonly string[] builderAgents = ["builder", "builder-xhigh", "builder-max"];

    private readonly string baseDir = Directory.CreateTempSubdirectory("claustrum-linked-dir-").FullName;
    private readonly string repo;
    private readonly string outside;
    private readonly string home;
    private readonly RoleLibrary library = new();

    public SyncLinkedDirectoryTests()
    {
        repo = Directory.CreateDirectory(Path.Combine(baseDir, "repo")).FullName;
        outside = Directory.CreateDirectory(Path.Combine(baseDir, "outside")).FullName;
        home = Directory.CreateDirectory(Path.Combine(baseDir, "home")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine\n");
    }

    public void Dispose() => LinkFixtures.DeleteTree(baseDir);

    public static TheoryData<string> Harnesses => [.. HarnessSyncs.All];

    public static TheoryData<string> HarnessesWithAManifest => ["claude", "opencode", "cursor"];

    public static TheoryData<SyncMode, bool> ModesAndForce => ModeMatrix();

    public static TheoryData<string, SyncMode, bool> HarnessModesAndForce => HarnessMatrix(HarnessSyncs.All);

    public static TheoryData<string, SyncMode, bool> ManifestHarnessModesAndForce => HarnessMatrix(["claude", "opencode", "cursor"]);

    private static TheoryData<SyncMode, bool> ModeMatrix()
    {
        TheoryData<SyncMode, bool> data = [];
        foreach (SyncMode mode in Enum.GetValues<SyncMode>())
        {
            data.Add(mode, false);
            data.Add(mode, true);
        }

        return data;
    }

    private static TheoryData<string, SyncMode, bool> HarnessMatrix(IEnumerable<string> harnesses)
    {
        TheoryData<string, SyncMode, bool> data = [];
        foreach (string harness in harnesses)
        {
            foreach (SyncMode mode in Enum.GetValues<SyncMode>())
            {
                data.Add(harness, mode, false);
                data.Add(harness, mode, true);
            }
        }

        return data;
    }

    private string Under(params string[] parts) => Path.Combine([baseDir, .. parts]);

    private SyncResult Run(string harness, SyncMode mode = SyncMode.Write, bool force = false, string? cwd = null) =>
        HarnessSyncs.Run(harness, library, home, cwd ?? repo, ["builder"], force: force, mode: mode);

    private static string[] AgentFiles(string harness, string root) =>
        [.. builderAgents.Select(name => HarnessSyncs.AgentFile(harness, root, name))];

    private static string[] AgentFilesAndSkill(string harness, string root) =>
        [.. AgentFiles(harness, root), HarnessSyncs.SkillFile(harness, root)];

    private static IReadOnlyDictionary<string, string> RefusedOf(SyncResult result)
    {
        Assert.NotNull(result.Refused);
        return result.Refused;
    }

    private static IReadOnlyDictionary<string, string> RealPathsOf(SyncResult result)
    {
        Assert.NotNull(result.RealPaths);
        return result.RealPaths;
    }

    /// <summary>
    /// Each path is refused, mapped to the same tail under <paramref name="realRoot"/>, in RealPaths, and in no other list.
    /// </summary>
    private static void AssertRefusedInto(SyncResult result, string linkedRoot, string realRoot, IEnumerable<string> paths)
    {
        IReadOnlyDictionary<string, string> refused = RefusedOf(result);
        IReadOnlyDictionary<string, string> realPaths = RealPathsOf(result);
        foreach (string path in paths)
        {
            string expected = Path.Combine(realRoot, Path.GetRelativePath(linkedRoot, path));
            Assert.True(refused.ContainsKey(path), $"not refused: {path}");
            Assert.Equal(expected, refused[path], LinkFixtures.PathComparer);
            Assert.Equal(expected, realPaths[path], LinkFixtures.PathComparer);
            Assert.DoesNotContain(path, result.Written);
            Assert.DoesNotContain(path, result.Skipped);
            Assert.DoesNotContain(path, result.Foreign);
            Assert.True(result.ProposedContent is null || !result.ProposedContent.ContainsKey(path), $"proposed: {path}");
        }
    }

    private static void WriteOverride(string cwd)
    {
        string roleDirectory = Directory.CreateDirectory(Path.Combine(cwd, ".claustrum", "roles", "builder")).FullName;
        File.WriteAllText(Path.Combine(roleDirectory, "ROLE.md"), $"# Builder\n\n{OverrideText}\n");
    }

    [Theory]
    [MemberData(nameof(HarnessModesAndForce))]
    public void ALinkedHarnessRootOutsideTheRepoRefusesEveryFileAndCreatesNothingThere(string harness, SyncMode mode, bool force)
    {
        string linked = HarnessSyncs.RepoRoot(harness, repo);
        LinkFixtures.DirectoryLink(linked, outside);
        string[] before = LinkFixtures.Snapshot(outside);

        SyncResult result = Run(harness, mode, force);

        AssertRefusedInto(result, linked, outside, AgentFilesAndSkill(harness, linked));
        Assert.DoesNotContain(result.Written, path => path.StartsWith(linked, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Skipped, path => path.StartsWith(linked, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Foreign, path => path.StartsWith(linked, StringComparison.Ordinal));
        Assert.Equal(before, LinkFixtures.Snapshot(outside));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(outside, "agents")));
        Assert.False(Directory.Exists(Path.Combine(outside, "skills")));
    }

    [Fact]
    public void AJunctionedHarnessRootOutsideTheRepoIsRefusedAndCreatesNothingThereOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("a junction is a Windows directory-link kind.");
            return;
        }

        string linked = HarnessSyncs.RepoRoot("claude", repo);
        LinkFixtures.Junction(linked, outside);
        string[] before = LinkFixtures.Snapshot(outside);

        SyncResult result = Run("claude");

        AssertRefusedInto(result, linked, outside, AgentFilesAndSkill("claude", linked));
        Assert.Equal(before, LinkFixtures.Snapshot(outside));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    // The target is a form .NET reports as relative, which Windows follows to the real directory (#95 review).
    [Fact]
    public void AHarnessRootLinkedThroughGlobalRootIsRefusedAndCreatesNothingThereOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("an NT-namespace link target is a Windows form.");
            return;
        }

        string linked = HarnessSyncs.RepoRoot("claude", repo);
        LinkFixtures.DirectoryLink(linked, @"\\?\GLOBALROOT\??\" + outside);
        string[] before = LinkFixtures.Snapshot(outside);

        SyncResult result = Run("claude");

        foreach (string path in AgentFilesAndSkill("claude", linked))
        {
            Assert.Contains(path, RefusedOf(result).Keys);
            Assert.DoesNotContain(path, result.Written);
            Assert.DoesNotContain(path, result.Skipped);
            Assert.DoesNotContain(path, result.Foreign);
        }

        Assert.Equal(before, LinkFixtures.Snapshot(outside));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Theory]
    [MemberData(nameof(HarnessesWithAManifest))]
    public void ALinkedHarnessRootKeepsTheManifestFreeOfTheRefusedFiles(string harness)
    {
        LinkFixtures.DirectoryLink(HarnessSyncs.RepoRoot(harness, repo), outside);

        Run(harness);

        string manifest = Path.Combine(repo, ".claustrum", "sync-manifest.json");
        Assert.True(File.Exists(manifest));
        Assert.DoesNotContain("builder", File.ReadAllText(manifest), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void OnlyTheAgentsDirectoryLinkedOutsideRefusesTheAgentFilesAndTheSkillIsWrittenNormally(string harness)
    {
        string root = Directory.CreateDirectory(HarnessSyncs.RepoRoot(harness, repo)).FullName;
        string agentDirectory = HarnessSyncs.AgentDirectory(harness, root);
        string agentStore = Directory.CreateDirectory(Under("agent-store")).FullName;
        LinkFixtures.DirectoryLink(agentDirectory, agentStore);

        SyncResult result = Run(harness);

        AssertRefusedInto(result, agentDirectory, agentStore, AgentFiles(harness, root));
        string skill = HarnessSyncs.SkillFile(harness, root);
        Assert.Contains(skill, result.Written);
        Assert.DoesNotContain(skill, RefusedOf(result).Keys);
        Assert.True(File.Exists(skill));
        Assert.Empty(Directory.GetFileSystemEntries(agentStore));
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void ALinkedHarnessRootThatStaysInsideTheRepoIsWrittenThroughAndAnnotated(string harness)
    {
        string inside = Directory.CreateDirectory(Path.Combine(repo, "real-harness-root")).FullName;
        string linked = HarnessSyncs.RepoRoot(harness, repo);
        LinkFixtures.DirectoryLink(linked, inside);

        SyncResult result = Run(harness);

        Assert.Empty(RefusedOf(result));
        foreach (string path in AgentFilesAndSkill(harness, linked))
        {
            string real = Path.Combine(inside, Path.GetRelativePath(linked, path));
            Assert.Contains(path, result.Written);
            Assert.True(File.Exists(real), $"not written through the link: {real}");
            Assert.Equal(real, RealPathsOf(result)[path], LinkFixtures.PathComparer);
        }
    }

    // The issue's repro: a repo whose `.claude` is the dotfiles link to ~/.claude, plus a local override of a role. Before
    // the fence a plain sync overwrote the user-global agent with the repo's `source=local` render.
    [Theory]
    [MemberData(nameof(ModesAndForce))]
    public void TheIssueReproLeavesTheLinkedGlobalAgentByteIdentical(SyncMode mode, bool force)
    {
        string globalRoot = Path.Combine(home, ".claude");
        HarnessSyncs.Run("claude", library, home, repo, ["builder"], global: true, globalRoot: globalRoot);
        string globalAgent = Path.Combine(globalRoot, "agents", "builder.md");
        byte[] before = File.ReadAllBytes(globalAgent);
        Assert.Contains("claustrum:generated", File.ReadAllText(globalAgent), StringComparison.Ordinal);
        string work = Directory.CreateDirectory(Path.Combine(home, "work", "repo")).FullName;
        LinkFixtures.DirectoryLink(Path.Combine(work, ".claude"), Path.GetRelativePath(work, globalRoot));
        WriteOverride(work);

        SyncResult result = Run("claude", mode, force, work);

        AssertRefusedInto(result, Path.Combine(work, ".claude"), globalRoot, AgentFiles("claude", Path.Combine(work, ".claude")));
        Assert.Equal(before, File.ReadAllBytes(globalAgent));
        string after = File.ReadAllText(globalAgent);
        Assert.DoesNotContain("source=local", after, StringComparison.Ordinal);
        Assert.DoesNotContain(OverrideText, after, StringComparison.Ordinal);
    }

    // Control for the repro: the same override into a real directory does render, so the absence above is the fence's doing.
    [Fact]
    public void TheLocalOverrideOfTheReproDoesRenderIntoARealDirectory()
    {
        string control = Directory.CreateDirectory(Under("control")).FullName;
        WriteOverride(control);

        Run("claude", cwd: control);

        string rendered = File.ReadAllText(Path.Combine(control, ".claude", "agents", "builder.md"));
        Assert.Contains("source=local", rendered, StringComparison.Ordinal);
        Assert.Contains(OverrideText, rendered, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void GlobalSyncThroughALinkedRootRefusesNothingAndWritesIntoTheRealDirectory(string harness)
    {
        string real = Directory.CreateDirectory(Under("real-global")).FullName;
        string linked = Under("linked-global");
        LinkFixtures.DirectoryLink(linked, real);

        SyncResult result = HarnessSyncs.Run(harness, library, home, repo, ["builder"], global: true, globalRoot: linked);

        Assert.Empty(RefusedOf(result));
        foreach (string path in AgentFilesAndSkill(harness, linked))
        {
            string target = Path.Combine(real, Path.GetRelativePath(linked, path));
            Assert.Contains(path, result.Written);
            Assert.True(File.Exists(target), $"not written into the real directory: {target}");
            Assert.Equal(target, RealPathsOf(result)[path], LinkFixtures.PathComparer);
        }
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void GlobalSyncIntoALinkedAgentsDirectoryTheStowCaseRefusesNothingAndWritesThroughIt(string harness)
    {
        string root = Directory.CreateDirectory(Under("global-root")).FullName;
        string stow = Directory.CreateDirectory(Under("stow", "agents")).FullName;
        string agentDirectory = HarnessSyncs.AgentDirectory(harness, root);
        LinkFixtures.DirectoryLink(agentDirectory, stow);

        SyncResult result = HarnessSyncs.Run(harness, library, home, repo, ["builder"], global: true, globalRoot: root);

        Assert.Empty(RefusedOf(result));
        foreach (string path in AgentFiles(harness, root))
        {
            string target = Path.Combine(stow, Path.GetFileName(path));
            Assert.Contains(path, result.Written);
            Assert.True(File.Exists(target), $"not written into the stow directory: {target}");
            Assert.Equal(target, RealPathsOf(result)[path], LinkFixtures.PathComparer);
        }
    }

    // The leaf link's target is where sync does not write: the file lands in the (linked) parent, and RealPaths says so.
    [Fact]
    public void AReplacedLeafLinkInsideALinkedDirectoryRecordsWhereTheFileLands()
    {
        string dotfiles = Directory.CreateDirectory(Path.Combine(home, "dotfiles", "agents")).FullName;
        string notes = Directory.CreateDirectory(Path.Combine(home, "notes")).FullName;
        File.WriteAllText(Path.Combine(notes, "builder.md"), "mine\n");
        string globalRoot = Directory.CreateDirectory(Path.Combine(home, ".claude")).FullName;
        LinkFixtures.DirectoryLink(Path.Combine(globalRoot, "agents"), Path.Combine("..", "dotfiles", "agents"));
        LinkFixtures.FileLink(Path.Combine(dotfiles, "builder.md"), Path.Combine("..", "..", "notes", "builder.md"));
        string agent = Path.Combine(globalRoot, "agents", "builder.md");

        SyncResult result = HarnessSyncs.Run("claude", library, home, repo, ["builder"], global: true, force: true, globalRoot: globalRoot);

        string landed = Path.Combine(dotfiles, "builder.md");
        Assert.Contains(agent, result.Written);
        Assert.NotNull(result.Symlinks);
        Assert.True(result.Symlinks.ContainsKey(agent));
        Assert.Equal(landed, RealPathsOf(result)[agent], LinkFixtures.PathComparer);
        Assert.Null(new FileInfo(landed).LinkTarget);
        Assert.Contains("claustrum:generated", File.ReadAllText(landed), StringComparison.Ordinal);
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(notes, "builder.md")));
    }

    // #84 under the fence: the link is the repo's own path, so it is foreign or replaced whatever it points at, never refused.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALeafLinkToAFileOutsideTheRepoIsForeignOrReplacedAndNeverRefused(bool force)
    {
        string agents = Directory.CreateDirectory(Path.Combine(repo, ".claude", "agents")).FullName;
        string agent = Path.Combine(agents, "builder.md");
        string target = Path.Combine(outside, "notes.md");
        File.WriteAllText(target, "my own notes\n");
        LinkFixtures.FileLink(agent, target);
        SyncAccumulator into = new();

        new SyncWriter("claude", "1.0.0").Write(agent, "builder", "---\nname: builder\n---", "Body.", force, SyncMode.Write, into, SyncRoot.Repo(repo));

        Assert.Empty(into.Refused);
        Assert.Equal("my own notes\n", File.ReadAllText(target));
        if (force)
        {
            Assert.Equal([agent], into.Written);
            Assert.Null(new FileInfo(agent).LinkTarget);
        }
        else
        {
            Assert.Equal([agent], into.Foreign);
            Assert.Empty(into.Written);
            Assert.Equal(target, new FileInfo(agent).LinkTarget);
        }
    }

    [Theory]
    [MemberData(nameof(ModesAndForce))]
    public void ALinkedVscodeDirectoryRefusesItsMcpJsonAndCreatesNothingOutside(SyncMode mode, bool force)
    {
        string vscode = Path.Combine(repo, ".vscode");
        LinkFixtures.DirectoryLink(vscode, outside);
        string[] before = LinkFixtures.Snapshot(outside);

        SyncResult result = Run("claude", mode, force);

        AssertRefusedInto(result, vscode, outside, [Path.Combine(vscode, "mcp.json")]);
        Assert.DoesNotContain(Path.Combine(repo, ".mcp.json"), RefusedOf(result).Keys);
        Assert.False(File.Exists(Path.Combine(outside, "mcp.json")));
        Assert.Equal(before, LinkFixtures.Snapshot(outside));
    }

    [Theory]
    [MemberData(nameof(ModesAndForce))]
    public void ALeafLinkedMcpJsonToAFileOutsideTheRepoIsRefusedAndItsTargetIsByteIdentical(SyncMode mode, bool force)
    {
        string target = Path.Combine(outside, "shared.json");
        File.WriteAllText(target, /*lang=json,strict*/ """{"mcpServers":{"other":{"command":"x"}}}""");
        byte[] before = File.ReadAllBytes(target);
        string leaf = Path.Combine(repo, ".mcp.json");
        LinkFixtures.FileLink(leaf, target);

        SyncResult result = Run("claude", mode, force);

        // The real path of a leaf link is its target, not the link's own name under the outside directory.
        Assert.Equal(target, RefusedOf(result)[leaf], LinkFixtures.PathComparer);
        Assert.DoesNotContain(leaf, result.Written);
        Assert.DoesNotContain(leaf, result.Skipped);
        Assert.DoesNotContain(leaf, result.Foreign);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.Equal(target, new FileInfo(leaf).LinkTarget);
    }

    [Fact]
    public void ALeafLinkedMcpJsonToAFileInsideTheRepoIsMergedThroughAndAnnotated()
    {
        string target = Path.Combine(repo, "real-mcp.json");
        File.WriteAllText(target, /*lang=json,strict*/ """{"mcpServers":{"other":{"command":"x"}}}""");
        string leaf = Path.Combine(repo, ".mcp.json");
        LinkFixtures.FileLink(leaf, target);

        SyncResult result = Run("claude");

        Assert.DoesNotContain(leaf, RefusedOf(result).Keys);
        Assert.Contains(leaf, result.Written);
        Assert.Equal(target, RealPathsOf(result)[leaf], LinkFixtures.PathComparer);
        Assert.NotNull(new FileInfo(leaf).LinkTarget);
        JsonObject servers = JsonNode.Parse(File.ReadAllText(target))?["mcpServers"]?.AsObject() ?? throw new InvalidOperationException("no mcpServers");
        Assert.NotNull(servers["claustrum"]);
        Assert.NotNull(servers["other"]);
    }

    [Fact]
    public void AGlobalCursorMcpJsonThatIsALeafLinkIsMergedThrough()
    {
        string cursorRoot = Directory.CreateDirectory(Under("global-cursor")).FullName;
        string target = Path.Combine(Directory.CreateDirectory(Under("dotfiles")).FullName, "mcp.json");
        File.WriteAllText(target, /*lang=json,strict*/ """{"mcpServers":{"other":{"command":"x"}}}""");
        string leaf = Path.Combine(cursorRoot, "mcp.json");
        LinkFixtures.FileLink(leaf, target);

        SyncResult result = new CursorSync(library, new RoleRenderer(library), home)
            .Sync(repo, ["builder"], global: true, globalBinaryPath: "/abs/claustrum", globalRoot: cursorRoot);

        Assert.Empty(RefusedOf(result));
        Assert.Contains(leaf, result.Written);
        Assert.NotNull(new FileInfo(leaf).LinkTarget);
        JsonObject servers = JsonNode.Parse(File.ReadAllText(target))?["mcpServers"]?.AsObject() ?? throw new InvalidOperationException("no mcpServers");
        Assert.Equal("/abs/claustrum", servers["claustrum"]?["command"]?.GetValue<string>());
        Assert.NotNull(servers["other"]);
    }

    [Theory]
    [MemberData(nameof(ManifestHarnessModesAndForce))]
    public void ALinkedClaustrumDirectoryRefusesTheManifestInEveryModeAndCreatesNothingOutside(string harness, SyncMode mode, bool force)
    {
        LinkFixtures.DirectoryLink(Path.Combine(repo, ".claustrum"), outside);
        string[] before = LinkFixtures.Snapshot(outside);

        SyncResult result = Run(harness, mode, force);

        AssertRefusedInto(result, Path.Combine(repo, ".claustrum"), outside, [Path.Combine(repo, ".claustrum", "sync-manifest.json")]);
        Assert.False(File.Exists(Path.Combine(outside, "sync-manifest.json")));
        Assert.Equal(before, LinkFixtures.Snapshot(outside));
    }

    [Theory]
    [MemberData(nameof(HarnessesWithAManifest))]
    public void AnUnparsableManifestBehindALinkedClaustrumDirectoryIsNeverReadAndNeverThrows(string harness)
    {
        LinkFixtures.DirectoryLink(Path.Combine(repo, ".claustrum"), outside);
        string theirs = Path.Combine(outside, "sync-manifest.json");
        File.WriteAllText(theirs, "not json");

        SyncResult result = Run(harness);

        Assert.Contains(Path.Combine(repo, ".claustrum", "sync-manifest.json"), RefusedOf(result).Keys);
        Assert.Equal("not json", File.ReadAllText(theirs));
    }

    // `.claude -> d/..` with `d -> ../../.claude/agents` reads as the repo lexically and as ~/.claude to the kernel.
    [Theory]
    [InlineData(SyncMode.Write)]
    [InlineData(SyncMode.DryRun)]
    [InlineData(SyncMode.Check)]
    public void TheDotDotBypassThroughALinkedDirectoryIsRefused(SyncMode mode)
    {
        string globalAgents = Directory.CreateDirectory(Path.Combine(home, ".claude", "agents")).FullName;
        string work = Directory.CreateDirectory(Path.Combine(home, "work", "repo")).FullName;
        LinkFixtures.DirectoryLink(Path.Combine(work, "d"), Path.Combine("..", "..", ".claude", "agents"));
        LinkFixtures.DirectoryLink(Path.Combine(work, ".claude"), Path.Combine("d", ".."));
        string[] before = LinkFixtures.Snapshot(Path.Combine(home, ".claude"));

        SyncResult result = Run("claude", mode, cwd: work);

        string linked = Path.Combine(work, ".claude");
        if (OperatingSystem.IsWindows())
        {
            // The printed real path is the lexical reading there, so only the refusal itself is asserted.
            foreach (string path in AgentFiles("claude", linked))
            {
                Assert.Contains(path, RefusedOf(result).Keys);
                Assert.DoesNotContain(path, result.Written);
                Assert.DoesNotContain(path, result.Skipped);
                Assert.DoesNotContain(path, result.Foreign);
            }
        }
        else
            AssertRefusedInto(result, linked, Path.Combine(home, ".claude"), AgentFiles("claude", linked));

        Assert.Empty(Directory.GetFileSystemEntries(globalAgents));
        Assert.Equal(before, LinkFixtures.Snapshot(Path.Combine(home, ".claude")));
        Assert.False(Directory.Exists(Path.Combine(work, "agents")));
    }
}
