using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// docs/PLAN.md §B4: idempotency by construction (marker + manifest), foreign-file protection,
// `--force` adoption. The `--global` scenario is intentionally not exercised here — see the
// Skip reason on GlobalSyncPlacesFilesUnderAFakeHome and the tester report (fault_in: code,
// src/Claustrum.Roles/Sync/ClaudeSync.cs).
public sealed class ClaudeSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-sync-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private ClaudeSync NewSync() => new(library, new RoleRenderer(library));

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

    [Fact(Skip = "ClaudeSync.Sync(global: true) calls Environment.GetFolderPath(SpecialFolder.UserProfile) " +
        "directly (src/Claustrum.Roles/Sync/ClaudeSync.cs, Sync method) instead of taking an injectable " +
        "home directory the way IPlatform.HomeDirectory does elsewhere in this codebase. Empirically, " +
        "overriding the USERPROFILE process environment variable does not change what " +
        "Environment.GetFolderPath returns on .NET 10/Windows, so there is no safe way to redirect " +
        "--global away from the real ~/.claude from a unit test. See tester report, fault_in: code.")]
    public void GlobalSyncPlacesFilesUnderAFakeHome()
    {
    }
}
