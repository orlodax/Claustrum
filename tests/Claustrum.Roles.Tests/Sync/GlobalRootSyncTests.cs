using System.Text.Json;
using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #83: each sync takes the global root as a value (`globalRoot`) the CLI resolved from the environment; null keeps the
// default under the caller-supplied home. The syncs never read the environment themselves, so a root handed in must be
// the only place a `--global` run touches, and the fake home and the repo must stay empty.
public sealed class GlobalRootSyncTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-global-root-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-global-root-home-").FullName;
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-global-root-target-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        Directory.Delete(cwd, recursive: true);
        Directory.Delete(fakeHome, recursive: true);
        Directory.Delete(scratch, recursive: true);
    }

    // A root that does not exist yet: Write has to create it, DryRun and Check must not.
    private string NewRoot => Path.Combine(scratch, "relocated");

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AGlobalSyncWithARootWritesEveryFileUnderItAndNothingUnderTheHomeOrTheRepo(string harness)
    {
        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: NewRoot);

        string agent = HarnessSyncs.AgentFile(harness, NewRoot, "builder");
        Assert.Contains(agent, result.Written);
        Assert.Contains(HarnessSyncs.AgentFile(harness, NewRoot, "builder-xhigh"), result.Written);
        Assert.Contains(HarnessSyncs.AgentFile(harness, NewRoot, "builder-max"), result.Written);
        Assert.Contains(HarnessSyncs.SkillFile(harness, NewRoot), result.Written);
        Assert.All(result.Written, path => Assert.StartsWith(NewRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal));
        Assert.True(File.Exists(agent));
        Assert.Contains("## Report format", File.ReadAllText(HarnessSyncs.AgentFile(harness, NewRoot, "builder-xhigh")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fakeHome));
        Assert.Empty(Directory.GetFileSystemEntries(cwd));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AGlobalSyncWithNoRootUsesTheHarnessDefaultUnderTheHome(string harness)
    {
        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: null);

        string root = HarnessSyncs.DefaultGlobalRoot(harness, fakeHome);
        Assert.Contains(HarnessSyncs.AgentFile(harness, root, "builder"), result.Written);
        Assert.Contains(HarnessSyncs.SkillFile(harness, root), result.Written);
        Assert.All(result.Written, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(cwd));
    }

    // The defaults the issue named, spelled out once so a change to a `DefaultGlobalRoot` shows here and not only as a moved file.
    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("copilot", ".copilot")]
    [InlineData("cursor", ".cursor")]
    public void TheDefaultGlobalRootIsADotDirectoryUnderTheHome(string harness, string directory) =>
        Assert.Equal(Path.Combine(fakeHome, directory), HarnessSyncs.DefaultGlobalRoot(harness, fakeHome));

    [Fact]
    public void TheOpencodeDefaultGlobalRootIsDotConfigOpencodeUnderTheHome() =>
        Assert.Equal(Path.Combine(fakeHome, ".config", "opencode"), HarnessSyncs.DefaultGlobalRoot("opencode", fakeHome));

    // A root only means something under --global: a stray value in repo mode must not redirect the repo's own files.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void ARootIsIgnoredWhenTheSyncIsNotGlobal(string harness)
    {
        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: false, globalRoot: NewRoot);

        Assert.Contains(HarnessSyncs.AgentFile(harness, HarnessSyncs.RepoRoot(harness, cwd), "builder"), result.Written);
        Assert.False(Directory.Exists(NewRoot));
        Assert.Empty(Directory.GetFileSystemEntries(fakeHome));
    }

    [Theory]
    [InlineData("claude", SyncMode.DryRun)]
    [InlineData("claude", SyncMode.Check)]
    [InlineData("opencode", SyncMode.DryRun)]
    [InlineData("opencode", SyncMode.Check)]
    [InlineData("copilot", SyncMode.DryRun)]
    [InlineData("copilot", SyncMode.Check)]
    [InlineData("cursor", SyncMode.DryRun)]
    [InlineData("cursor", SyncMode.Check)]
    public void DryRunAndCheckNameFilesUnderTheRootWithoutCreatingIt(string harness, SyncMode mode)
    {
        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, mode: mode, globalRoot: NewRoot);

        Assert.Contains(HarnessSyncs.AgentFile(harness, NewRoot, "builder"), result.Written);
        Assert.False(Directory.Exists(NewRoot));
        Assert.Empty(Directory.GetFileSystemEntries(fakeHome));
        Assert.Empty(Directory.GetFileSystemEntries(cwd));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void ASecondGlobalSyncIntoTheSameRootIsAllUpToDate(string harness)
    {
        HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: NewRoot);

        SyncResult second = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: NewRoot);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.Contains(HarnessSyncs.AgentFile(harness, NewRoot, "builder"), second.Skipped);
    }

    // Cursor reads mcp.json from the same root as its agents, so the relocated root takes it along; the claude
    // desktop config is a separate file with its own path and must not move with `globalRoot`.
    [Fact]
    public void CursorsGlobalMcpJsonFollowsTheRoot()
    {
        CursorSync sync = new(library, new RoleRenderer(library), fakeHome);

        SyncResult result = sync.Sync(cwd, ["builder"], global: true, globalBinaryPath: "/abs/claustrum", globalRoot: NewRoot);

        string mcp = Path.Combine(NewRoot, "mcp.json");
        Assert.Contains(mcp, result.Written);
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(mcp));
        Assert.Equal("/abs/claustrum", config.RootElement.GetProperty("mcpServers").GetProperty("claustrum").GetProperty("command").GetString());
        Assert.False(File.Exists(Path.Combine(fakeHome, ".cursor", "mcp.json")));
    }

    [Fact]
    public void TheClaudeDesktopConfigKeepsItsOwnPathWhenTheAgentRootMoves()
    {
        string desktopConfig = Path.Combine(scratch, "desktop", "claude_desktop_config.json");
        ClaudeSync sync = new(library, new RoleRenderer(library), fakeHome);

        SyncResult result = sync.Sync(cwd, ["builder"], global: true, desktop: new ClaudeDesktopTarget(desktopConfig, "/abs/claustrum"), globalRoot: NewRoot);

        Assert.Contains(desktopConfig, result.Written);
        Assert.Contains(HarnessSyncs.AgentFile("claude", NewRoot, "builder"), result.Written);
        Assert.True(File.Exists(desktopConfig));
        Assert.False(File.Exists(Path.Combine(NewRoot, "claude_desktop_config.json")));
    }
}
