using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// docs/PLAN.md §B4: idempotency by construction (marker + manifest), foreign-file protection,
// `--force` adoption. `--global` is exercised via `fakeHome`, a per-test temp directory injected as
// ClaudeSync's `homeDirectory` constructor parameter, so it never touches the real `~/.claude`.
public sealed class ClaudeSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-sync-home-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
    }

    private ClaudeSync NewSync() => new(library, new RoleRenderer(library), fakeHome);

    [Fact]
    public void FreshSyncWritesBaseAgentTierStubsAndSkill()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith("builder-xhigh.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith("builder-max.md", StringComparison.Ordinal));
        Assert.Contains(result.Written, p => p.EndsWith(Path.Combine("skills", "delegate", "SKILL.md"), StringComparison.Ordinal));
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Foreign);
    }

    [Fact]
    public void RerunIsFullyIdempotent()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult second = sync.Sync(cwd, roles: ["builder"]);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.NotEmpty(second.Skipped);
    }

    [Fact]
    public void CrlfCheckoutOfAGeneratedFileIsStillSkipped()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));

        SyncResult result = sync.Sync(cwd, roles: ["builder"]);

        Assert.Contains(path, result.Skipped);
        Assert.DoesNotContain(path, result.Written);
    }

    [Fact]
    public void ForeignFileWithoutMarkerIsLeftUntouchedWithoutForce()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: false);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void ForeignFileIsAdoptedWithForce()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], force: true);

        Assert.Contains(path, result.Written);
        Assert.Contains("claustrum:generated", File.ReadAllText(path));
    }

    [Fact]
    public void SyncWritesTheManifestWithEveryGeneratedFile()
    {
        NewSync().Sync(cwd, roles: ["builder"]);

        string manifestPath = Path.Combine(cwd, ".claustrum", "sync-manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("builder.md", File.ReadAllText(manifestPath));
    }

    [Fact]
    public void GlobalSyncPlacesFilesUnderAFakeHome()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], global: true);

        string expectedPath = Path.Combine(fakeHome, ".claude", "agents", "builder.md");
        Assert.Contains(result.Written, p => p == expectedPath);
        Assert.True(File.Exists(expectedPath));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public void DryRunClassifiesWithoutTouchingDisk()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.DryRun);

        Assert.Contains(result.Written, p => p.EndsWith("builder.md", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
        Assert.NotNull(result.ProposedContent);
        Assert.Contains(result.ProposedContent!.Keys, p => p.EndsWith("builder.md", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckOnAFreshRepoReportsEverythingMissingWithoutWriting()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.NotEmpty(result.Written);
        Assert.False(Directory.Exists(Path.Combine(cwd, ".claude")));
    }

    [Fact]
    public void CheckAfterARealSyncReportsNothingOutstanding()
    {
        ClaudeSync sync = NewSync();
        sync.Sync(cwd, roles: ["builder"]);

        SyncResult result = sync.Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Empty(result.Written);
        Assert.Empty(result.Foreign);
        Assert.NotEmpty(result.Skipped);
    }

    [Fact]
    public void CheckStillDetectsAForeignFileWithoutAdoptingIt()
    {
        string path = Path.Combine(cwd, ".claude", "agents", "builder.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "hand-written content, no marker\n");

        SyncResult result = NewSync().Sync(cwd, roles: ["builder"], mode: SyncMode.Check);

        Assert.Contains(path, result.Foreign);
        Assert.Equal("hand-written content, no marker\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteModeNeverPopulatesProposedContent()
    {
        SyncResult result = NewSync().Sync(cwd, roles: ["builder"]);

        Assert.Null(result.ProposedContent);
    }
}
