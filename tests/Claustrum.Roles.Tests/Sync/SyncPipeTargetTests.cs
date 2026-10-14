using Claustrum.Core.IO;
using Claustrum.Roles.Model;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// 2026-10-13 (found by review): a FIFO standing where sync reads or writes blocked the whole command, since File.Exists is true
// for it and the read opens it. Each file sync reads first (an agent file, .mcp.json, the manifest) is refused with the reason,
// nothing is opened for writing, and the run goes on. Unix only (mkfifo).
public sealed class SyncPipeTargetTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-pipe-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-pipe-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(fakeHome);
    }

    private static IReadOnlyDictionary<string, string> Refusals(SyncResult result) => Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(result.Refused);

    public static TheoryData<bool, SyncMode> ForceAndMode() => new()
    {
        { false, SyncMode.Write },
        { true, SyncMode.Write },
        { false, SyncMode.DryRun },
        { true, SyncMode.DryRun },
        { false, SyncMode.Check },
        { true, SyncMode.Check },
    };

    [Theory]
    [MemberData(nameof(ForceAndMode))]
    public void ASyncWriterTargetThatIsAFifoIsRefusedWithTheReasonAndNothingIsWritten(bool force, SyncMode mode)
    {
        string path = Path.Combine(cwd, "builder.md");
        PipeFiles.MakeFifo(path);
        SyncWriter writer = new("claude", "1.0.0");
        SyncAccumulator into = new();

        PipeFiles.Within(() =>
        {
            writer.Write(path, "builder", "---\nname: builder\n---", "Body.", force, mode, into, SyncRoot.Global(cwd));
            return 0;
        });

        Assert.Contains("not a regular file", into.Refused[path], StringComparison.Ordinal);
        Assert.Empty(into.Written);
        Assert.Empty(into.Skipped);
        Assert.Empty(into.Foreign);
        Assert.Empty(into.ProposedContent);
        Assert.Empty(into.ManifestFiles);
    }

    [Fact]
    public void AFifoTargetIsStillAFifoAfterAForcedWrite()
    {
        string path = Path.Combine(cwd, "builder.md");
        PipeFiles.MakeFifo(path);
        SyncAccumulator into = new();

        PipeFiles.Within(() =>
        {
            new SyncWriter("claude", "1.0.0").Write(path, "builder", "---\nname: builder\n---", "Body.", force: true, SyncMode.Write, into, SyncRoot.Global(cwd));
            return 0;
        });

        // Still a pipe: a regular file written over it would open.
        Assert.Null(PlainFile.OpenRead(path, out string? stillRefused));
        Assert.NotNull(stillRefused);
        Assert.Empty(into.Written);
    }

    [Fact]
    public void AnMcpJsonThatIsAFifoIsRefusedWithoutBlocking()
    {
        PipeFiles.MakeFifo(Path.Combine(cwd, ".mcp.json"));

        SyncResult result = PipeFiles.Within(() => new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: ["builder"]));

        string refusal = Refusals(result)[Path.Combine(cwd, ".mcp.json")];
        Assert.Contains("not a regular file", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine(cwd, ".mcp.json"), result.Written);
        Assert.Contains(Path.Combine(cwd, ".claude", "agents", "builder.md"), result.Written);
    }

    [Fact]
    public void AnOpencodeJsonThatIsAFifoIsRefusedWithoutBlocking()
    {
        PipeFiles.MakeFifo(Path.Combine(cwd, "opencode.json"));

        SyncResult result = PipeFiles.Within(() => new OpencodeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: ["builder"]));

        Assert.Contains("not a regular file", Refusals(result)[Path.Combine(cwd, "opencode.json")], StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestThatIsAFifoReadsEmptyAndIsRecordedAsRefused()
    {
        string manifest = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest) ?? cwd);
        PipeFiles.MakeFifo(manifest);
        SyncAccumulator into = new();

        Dictionary<string, SyncManifestFile> read = PipeFiles.Within(() => SyncManifestStore.Read(cwd, into));

        Assert.Empty(read);
        Assert.Contains("not a regular file", into.Refused[manifest], StringComparison.Ordinal);
    }

    // Update on its own, with no earlier Read: the write handle is checked too (2026-10-14), so it never blocks on the pipe.
    [Fact]
    public void UpdateOnItsOwnWritesNothingThroughAFifoManifest()
    {
        string manifest = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest) ?? cwd);
        PipeFiles.MakeFifo(manifest);
        SyncAccumulator into = new();
        into.ManifestFiles.Add(new SyncManifestFile(Path.Combine(cwd, "x.md"), "builder", "claude", "abc"));

        PipeFiles.Within(() =>
        {
            SyncManifestStore.Update(cwd, "1.0.0", into);
            return 0;
        });

        Assert.Contains("not a regular file", into.Refused[manifest], StringComparison.Ordinal);
        Assert.False(new FileInfo(manifest).Length > 0);
    }

    [Fact]
    public void AFifoAgentFileDoesNotStopTheOtherAgentsBeingWritten()
    {
        string agents = Directory.CreateDirectory(Path.Combine(cwd, ".claude", "agents")).FullName;
        PipeFiles.MakeFifo(Path.Combine(agents, "builder.md"));

        SyncResult result = PipeFiles.Within(() => new ClaudeSync(library, new RoleRenderer(library), fakeHome).Sync(cwd, roles: ["builder", "tester"]));

        Assert.Contains("not a regular file", Refusals(result)[Path.Combine(agents, "builder.md")], StringComparison.Ordinal);
        Assert.Contains(Path.Combine(agents, "tester.md"), result.Written);
    }
}
