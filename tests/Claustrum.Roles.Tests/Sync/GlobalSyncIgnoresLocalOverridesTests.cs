using Claustrum.Roles.Sync;

namespace Claustrum.Roles.Tests.Sync;

// #99: `sync --global` renders the library alone. A repo's `.claustrum/roles/` applies to that repo, and used to leak into the
// user-global agents of every other project; the syncs now pass a null cwd to the renderer under `--global` and record the
// roles whose override they left out (SyncResult.IgnoredOverrides). Repo mode is the control: the same override renders there.
public sealed class GlobalSyncIgnoresLocalOverridesTests : IDisposable
{
    private const string OverrideBody = "LOCAL-OVERRIDE-BODY";

    private static readonly string[] agentNames = ["builder", "builder-xhigh", "builder-max"];

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-global-override-").FullName;
    private readonly string fakeHome = Directory.CreateTempSubdirectory("claustrum-global-override-home-").FullName;
    private readonly string scratch = Directory.CreateTempSubdirectory("claustrum-global-override-root-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose()
    {
        LinkFixtures.DeleteTree(cwd);
        LinkFixtures.DeleteTree(fakeHome);
        LinkFixtures.DeleteTree(scratch);
    }

    private string GlobalRoot => Path.Combine(scratch, "global");

    private string RoleDirectory(string role) => Path.Combine(cwd, ".claustrum", "roles", role);

    private void WriteRoleMd(string role)
    {
        Directory.CreateDirectory(RoleDirectory(role));
        File.WriteAllText(Path.Combine(RoleDirectory(role), "ROLE.md"), $"{OverrideBody} for {role}\n");
    }

    private static string[] AgentFiles(string harness, string root) =>
        [.. agentNames.Select(name => HarnessSyncs.AgentFile(harness, root, name))];

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AGlobalSyncIgnoresTheReposOverrideAndNamesTheRoleItLeftOut(string harness)
    {
        WriteRoleMd("builder");

        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: GlobalRoot);

        Assert.Equal(["builder"], result.IgnoredOverrides);
        foreach (string file in AgentFiles(harness, GlobalRoot))
        {
            Assert.True(File.Exists(file), $"not written: {file}");
            string content = File.ReadAllText(file);
            Assert.DoesNotContain(OverrideBody, content, StringComparison.Ordinal);
            Assert.DoesNotContain("source=local", content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void TheSameOverrideRendersInRepoModeMarkedSourceLocalAndIgnoresNothing(string harness)
    {
        WriteRoleMd("builder");

        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"]);

        Assert.Empty(result.IgnoredOverrides ?? []);
        foreach (string file in AgentFiles(harness, HarnessSyncs.RepoRoot(harness, cwd)))
        {
            string content = File.ReadAllText(file);
            Assert.Contains(OverrideBody, content, StringComparison.Ordinal);
            Assert.Contains("source=local", content, StringComparison.Ordinal);
        }
    }

    // Only the roles that have an override are named, and a `role.json`-only or a `parts/`-only override counts as one.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void OnlyTheRolesWithAnOverrideOfAnyKindAreNamedInRoleOrder(string harness)
    {
        string tester = RoleDirectory("tester");
        Directory.CreateDirectory(tester);
        File.WriteAllText(Path.Combine(tester, "role.json"), /*lang=json,strict*/ """{"description":"Local tester"}""");
        string parts = Path.Combine(RoleDirectory("code-reviewer"), "parts");
        Directory.CreateDirectory(parts);
        File.WriteAllText(Path.Combine(parts, "anything.md"), "x");
        WriteRoleMd("builder");

        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder", "code-reviewer", "tester", "architect"], global: true, globalRoot: GlobalRoot);

        Assert.Equal(["builder", "code-reviewer", "tester"], result.IgnoredOverrides);
    }

    // The probe is existence-only: a hostile link in the repo is "something is there", and `--global` neither reads nor throws on it.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AGlobalSyncOverARepoWhoseOverrideIsALinkOutNeverReadsItAndStillNamesTheRole(string harness)
    {
        string outside = Directory.CreateDirectory(Path.Combine(scratch, "outside")).FullName;
        string secret = Path.Combine(outside, "secret.md");
        File.WriteAllText(secret, "TOP-SECRET-TOKEN\n");
        Directory.CreateDirectory(RoleDirectory("builder"));
        LinkFixtures.FileLink(Path.Combine(RoleDirectory("builder"), "ROLE.md"), secret);

        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: GlobalRoot);

        Assert.Equal(["builder"], result.IgnoredOverrides);
        foreach (string file in AgentFiles(harness, GlobalRoot))
            Assert.DoesNotContain("TOP-SECRET-TOKEN", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void ARepoWithNoOverrideIgnoresNothingUnderGlobal(string harness)
    {
        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: GlobalRoot);

        Assert.Empty(result.IgnoredOverrides ?? []);
    }

    // The notice rides every mode: the sync does not write in DryRun and Check, but it still looked at the override.
    [Theory]
    [InlineData("claude", SyncMode.DryRun)]
    [InlineData("claude", SyncMode.Check)]
    [InlineData("opencode", SyncMode.DryRun)]
    [InlineData("opencode", SyncMode.Check)]
    [InlineData("copilot", SyncMode.DryRun)]
    [InlineData("copilot", SyncMode.Check)]
    [InlineData("cursor", SyncMode.DryRun)]
    [InlineData("cursor", SyncMode.Check)]
    public void TheIgnoredOverridesAreReportedWithoutWritingInDryRunAndCheck(string harness, SyncMode mode)
    {
        WriteRoleMd("builder");

        SyncResult result = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, mode: mode, globalRoot: GlobalRoot);

        Assert.Equal(["builder"], result.IgnoredOverrides);
        Assert.False(Directory.Exists(GlobalRoot));
        foreach (string proposed in result.ProposedContent?.Values ?? [])
            Assert.DoesNotContain(OverrideBody, proposed, StringComparison.Ordinal);
    }

    // A global file written earlier from the library is not "stale" because the repo grew an override: the repo is not an input.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot")]
    [InlineData("cursor")]
    public void AnOverrideAddedAfterAGlobalSyncDoesNotMakeTheGlobalFilesStale(string harness)
    {
        HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, globalRoot: GlobalRoot);
        WriteRoleMd("builder");

        SyncResult second = HarnessSyncs.Run(harness, library, fakeHome, cwd, ["builder"], global: true, mode: SyncMode.Check, globalRoot: GlobalRoot);

        Assert.Empty(second.Written);
        Assert.Empty(second.Foreign);
        Assert.Contains(HarnessSyncs.AgentFile(harness, GlobalRoot, "builder"), second.Skipped);
        Assert.Equal(["builder"], second.IgnoredOverrides);
    }
}
