using Claustrum.Cli;
using Claustrum.Coordination;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Coordination;

// #74: the architect runs in a worktree, which holds committed files only, so `ArchitectWorktree.RequireReadyAsync`
// refuses — before a job exists — whatever the worktree would lack: an uncommitted cast, claustrum.json or role
// file, a missing ignore rule for Claustrum's own machinery, a subdirectory cwd. Every case is a real repository
// (IsolatedRepo.ForCoordinate: the rules, the cast and the config committed) perturbed one way, and the refusal
// is read off the message the operator would see. NOTES.md "coordinate runs the architect in its own worktree",
// 1–1d and G1, hold the measured behaviour of git behind each row.
public sealed class ArchitectWorktreeTests : IDisposable
{
    private const string CommitFirst = "which sees only committed files";

    private readonly IsolatedRepo repo = IsolatedRepo.ForCoordinate();

    public void Dispose() => repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string At(params string[] parts) => Path.Combine([repo.Repo, .. parts]);

    private Task<string[]> ReadyAsync(string castName = "default", string? cwd = null) =>
        ArchitectWorktree.RequireReadyAsync(cwd ?? repo.Repo, repo.Repo, castName, Ct);

    private async Task<string> RefusalAsync(string castName = "default")
    {
        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(() => ReadyAsync(castName));
        return refusal.Message;
    }

    private void CommitIgnore(string rules)
    {
        File.WriteAllText(At(".gitignore"), rules);
        repo.Git("add", ".gitignore");
        repo.Git("commit", "-q", "-m", "rules");
    }

    private void WriteCast(string name) =>
        File.WriteAllText(At(".claustrum", "casts", $"{name}.json"), File.ReadAllText(At(".claustrum", "casts", "default.json")));

    private void WriteRole(string role, string file, string content = "x\n")
    {
        string path = At(".claustrum", "roles", role, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
        File.WriteAllText(path, content);
    }

    private string[] ProbeDirectories()
    {
        string root = At(".claustrum", "worktrees");
        return Directory.Exists(root) ? [.. Directory.GetFileSystemEntries(root)] : [];
    }

    [Fact]
    public async Task ACommittedCleanRepositoryIsReadyAndWarnsOfNothingAsync()
    {
        Assert.Empty(await ReadyAsync());
        // The probe it asked git about is gone, and so is the `.claustrum/worktrees` made only for it.
        Assert.Empty(ProbeDirectories());
        Assert.False(Directory.Exists(At(".claustrum", "worktrees")));
    }

    [Fact]
    public async Task AnExistingWorktreesDirectoryKeepsItsChildrenAndLosesOnlyTheProbeAsync()
    {
        Directory.CreateDirectory(At(".claustrum", "worktrees", "someone-elses-job"));

        Assert.Empty(await ReadyAsync());

        Assert.Equal([At(".claustrum", "worktrees", "someone-elses-job")], ProbeDirectories());
    }

    // ---- the committed-files check: cast and claustrum.json --------------------------------------------------

    public static TheoryData<string> CommittedFiles => ["claustrum.json", ".claustrum/casts/default.json"];

    [Theory]
    [MemberData(nameof(CommittedFiles))]
    public async Task AModifiedFileIsRefusedNamingItAndItsStateAsync(string file)
    {
        File.AppendAllText(At(file), "\n");

        string message = await RefusalAsync();

        Assert.Contains($"coordinate runs the architect in a worktree, which sees only committed files — commit (or un-ignore) {file} (uncommitted changes) first", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CommittedFiles))]
    public async Task AFileDeletedButNotCommittedIsRefusedAsync(string file)
    {
        File.Delete(At(file));

        Assert.Contains($"commit (or un-ignore) {file} (uncommitted changes) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CommittedFiles))]
    public async Task AModificationStagedButNotCommittedIsRefusedAsync(string file)
    {
        File.AppendAllText(At(file), "\n");
        repo.Git("add", file);

        Assert.Contains($"commit (or un-ignore) {file} (uncommitted changes) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACastStagedAsNewButNeverCommittedIsRefusedAsync()
    {
        WriteCast("other");
        repo.Git("add", ".claustrum/casts/other.json");

        Assert.Contains("commit (or un-ignore) .claustrum/casts/other.json (uncommitted changes) first", await RefusalAsync("other"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntrackedCastIsRefusedAsync()
    {
        WriteCast("other");

        Assert.Contains("commit (or un-ignore) .claustrum/casts/other.json (untracked) first", await RefusalAsync("other"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredCastIsRefusedAsync()
    {
        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + ".claustrum/casts/other.json\n");
        WriteCast("other");

        Assert.Contains("commit (or un-ignore) .claustrum/casts/other.json (ignored) first", await RefusalAsync("other"), StringComparison.Ordinal);
    }

    // IsolatedRepo's own default: the whole of `.claustrum/` ignored, which hides the cast the worktree needs.
    [Fact]
    public async Task ARepositoryThatIgnoresAllOfDotClaustrumRefusesItsCastAsIgnoredAsync()
    {
        using IsolatedRepo wide = new();
        wide.WriteCast(maxParallel: null);

        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(
            () => ArchitectWorktree.RequireReadyAsync(wide.Repo, wide.Repo, "default", Ct));

        Assert.Contains("commit (or un-ignore) .claustrum/casts/default.json (ignored) first", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntrackedClaustrumJsonIsRefusedAndSoIsAnIgnoredOneAsync()
    {
        repo.Git("rm", "-q", "--cached", "claustrum.json");
        repo.Git("commit", "-q", "-m", "untrack the config");
        Assert.Contains("commit (or un-ignore) claustrum.json (untracked) first", await RefusalAsync(), StringComparison.Ordinal);

        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + "claustrum.json\n");
        Assert.Contains("commit (or un-ignore) claustrum.json (ignored) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    // `update-index --skip-worktree` / `--assume-unchanged` hide a local edit from `status` (F2): the architect's
    // Prepare would read the operator's edited file while its children read HEAD's.
    // A repository that ignores all of `.claustrum/` (what IsolatedRepo does by default, and what a project that keeps its
    // casts out of version control would) is ready once the files the worktree needs are force-added: ignored AND tracked.
    [Fact]
    public async Task ACastAndRoleFileForceAddedUnderAnIgnoredDotClaustrumAreReadyAsync()
    {
        using IsolatedRepo wide = new();
        wide.WriteCast(maxParallel: null);
        Directory.CreateDirectory(Path.Combine(wide.Repo, ".claustrum", "roles", "tester"));
        File.WriteAllText(Path.Combine(wide.Repo, ".claustrum", "roles", "tester", "ROLE.md"), "x\n");
        wide.Git("add", "-f", ".claustrum/casts/default.json", ".claustrum/roles/tester/ROLE.md");
        wide.Git("commit", "-q", "-m", "force-add the cast and a role");

        Assert.Empty(await ArchitectWorktree.RequireReadyAsync(wide.Repo, wide.Repo, "default", Ct));
    }

    [Theory]
    [InlineData("--skip-worktree", "skip-worktree")]
    [InlineData("--assume-unchanged", "assume-unchanged")]
    public async Task AHiddenLocalEditOfClaustrumJsonIsRefusedNamingTheBitAsync(string flag, string state)
    {
        repo.Git("update-index", flag, "claustrum.json");
        File.AppendAllText(At("claustrum.json"), "\n");
        Assert.Equal("", repo.Git("status", "--porcelain"));

        Assert.Contains($"commit (or un-ignore) claustrum.json ({state}) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderShowUntrackedFilesNoAnUntrackedCastIsStillRefusedAsync()
    {
        repo.Git("config", "status.showUntrackedFiles", "no");
        WriteCast("other");
        Assert.Equal("", repo.Git("status", "--porcelain"));

        Assert.Contains("commit (or un-ignore) .claustrum/casts/other.json (untracked) first", await RefusalAsync("other"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUntrackedCastWhoseNameHasASpaceOrNonAsciiCharactersIsNamedUnquotedAsync()
    {
        WriteCast("sp ace");
        WriteCast("café");

        Assert.Contains("commit (or un-ignore) .claustrum/casts/sp ace.json (untracked) first", await RefusalAsync("sp ace"), StringComparison.Ordinal);
        Assert.Contains("commit (or un-ignore) .claustrum/casts/café.json (untracked) first", await RefusalAsync("café"), StringComparison.Ordinal);
    }

    // A cast name is the caller's text: asked to git as a pathspec it would be a glob, and `--cast 'o*'` would be judged by
    // the state of every cast starting with `o`. The check is literal (`--literal-pathspecs`).
    [Fact]
    public async Task ACastNameWithAGlobCharacterIsJudgedByItsOwnFileAloneAsync()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows does not allow `*` in a file name.");

        WriteCast("other");
        WriteCast("o*");
        repo.CommitAll("two casts");
        File.AppendAllText(At(".claustrum", "casts", "other.json"), "\n");

        Assert.Empty(await ReadyAsync("o*"));
        Assert.Contains("commit (or un-ignore) .claustrum/casts/other.json (uncommitted changes) first", await RefusalAsync("other"), StringComparison.Ordinal);
    }

    // ---- the committed-files check: .claustrum/roles ----------------------------------------------------------

    [Theory]
    [InlineData("tester", "ROLE.md")]
    [InlineData("tester", "role.json")]
    [InlineData("builder", "parts/review.default.md")]
    public async Task AnUntrackedFileTheRoleLibraryReadsIsRefusedAsync(string role, string file)
    {
        WriteRole(role, file);

        Assert.Contains($"commit (or un-ignore) .claustrum/roles/{role}/{file} (untracked) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModifiedTrackedRoleFileIsRefusedAsync()
    {
        WriteRole("tester", "ROLE.md");
        repo.CommitAll("local role");
        File.AppendAllText(At(".claustrum", "roles", "tester", "ROLE.md"), "edited\n");

        Assert.Contains("commit (or un-ignore) .claustrum/roles/tester/ROLE.md (uncommitted changes) first", await RefusalAsync(), StringComparison.Ordinal);
    }

    // G1: an ignored `.DS_Store`, an editor's swap file and an untracked note are nobody's input — RoleLibrary reads
    // `<role>/role.json`, `<role>/ROLE.md` and `<role>/parts/<part>.<harness|default>.md` and nothing else.
    [Fact]
    public async Task JunkUnderTheRolesDirectoryDoesNotRefuseAsync()
    {
        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + ".DS_Store\n*.swp\n");
        WriteRole("tester", ".DS_Store");
        WriteRole("tester", ".ROLE.md.swp");
        WriteRole("tester", "parts/.DS_Store");
        WriteRole("tester", "notes.txt");

        Assert.Empty(await ReadyAsync());
    }

    [Fact]
    public async Task OnlyTheRoleFilesAreNamedWhenJunkSitsBesideThemAsync()
    {
        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + ".DS_Store\n");
        WriteRole("tester", ".DS_Store");
        WriteRole("tester", "notes.txt");
        WriteRole("tester", "ROLE.md");

        string message = await RefusalAsync();

        Assert.Contains("commit (or un-ignore) .claustrum/roles/tester/ROLE.md (untracked) first", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".DS_Store", message, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanFiveRefusedPathsAreCountedNotListedAsync()
    {
        for (int index = 1; index <= 7; index++)
            WriteRole($"r{index}", "ROLE.md");

        string message = await RefusalAsync();

        Assert.Contains("(and 2 more) first", message, StringComparison.Ordinal);
        Assert.Contains(".claustrum/roles/r1/ROLE.md (untracked)", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".claustrum/roles/r6/ROLE.md", message, StringComparison.Ordinal);
    }

    // ---- the ignore rules for Claustrum's own machinery -------------------------------------------------------

    private const string Rules = "git-ignore";

    [Fact]
    public async Task NoIgnoreRuleAtAllNamesAllThreeMachineryDirectoriesAsync()
    {
        CommitIgnore("");

        string message = await RefusalAsync();

        Assert.Contains("coordinate runs the architect in a worktree and commits what it leaves there — git-ignore .claustrum/worktrees/, .claustrum/briefs/, .claustrum/locks/ in a committed .gitignore first (`claustrum init` writes these rules)", message, StringComparison.Ordinal);
        Assert.DoesNotContain(CommitFirst, message, StringComparison.Ordinal);
        Assert.Empty(ProbeDirectories());
        Assert.False(Directory.Exists(At(".claustrum", "worktrees")));
    }

    [Fact]
    public async Task OnlyTheMissingRulesAreNamedAsync()
    {
        CommitIgnore(".claustrum/worktrees/\n");

        string message = await RefusalAsync();

        Assert.Contains($"{Rules} .claustrum/briefs/, .claustrum/locks/ in a committed .gitignore", message, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Rules} .claustrum/worktrees/", message, StringComparison.Ordinal);
    }

    // F1: the first cut probed a file named `probe` and a whitelist `.gitignore` ignores that through `*` — while its
    // `!*/` un-ignores a worktree directory and its `!*.md` a brief, which the runner's commit would then take in.
    [Fact]
    public async Task AWhitelistGitignoreIsRefusedForWorktreesAndBriefsButNotLocksAsync()
    {
        CommitIgnore("*\n!*/\n!*.json\n!*.md\n!*.txt\n!.gitignore\n");

        string message = await RefusalAsync();

        Assert.Contains($"{Rules} .claustrum/worktrees/, .claustrum/briefs/ in a committed .gitignore first", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".claustrum/locks/", message, StringComparison.Ordinal);
        Assert.Empty(ProbeDirectories());
        Assert.False(Directory.Exists(At(".claustrum", "worktrees")));
    }

    [Fact]
    public async Task ANegatedRuleIsAMatchThatUnIgnoresAndIsRefusedAsync()
    {
        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + "!.claustrum/briefs/\n");

        Assert.Contains($"{Rules} .claustrum/briefs/ in a committed .gitignore first", await RefusalAsync(), StringComparison.Ordinal);
    }

    // G1/R6: rules the worktree will not have are not rules. The working-tree .gitignore has them, HEAD's does not.
    [Fact]
    public async Task RulesOnlyInAnUncommittedGitignoreAreRefusedNamingThatFileAsync()
    {
        CommitIgnore("");
        File.WriteAllText(At(".gitignore"), IsolatedRepo.MachineryIgnoreRules);

        string message = await RefusalAsync();

        Assert.Contains($"coordinate runs the architect in a worktree, {CommitFirst} — .gitignore (uncommitted changes — the worktree gets HEAD's ignore rules; commit it first)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("commit (or un-ignore)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("git-ignore .claustrum", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesStagedButNotCommittedInGitignoreAreRefusedTooAsync()
    {
        CommitIgnore("");
        File.WriteAllText(At(".gitignore"), IsolatedRepo.MachineryIgnoreRules);
        repo.Git("add", ".gitignore");

        Assert.Contains(".gitignore (uncommitted changes — the worktree gets HEAD's ignore rules; commit it first)", await RefusalAsync(), StringComparison.Ordinal);
    }

    // `.git/info/exclude` is shared by every worktree: rules there are enough, and nothing needs committing.
    [Fact]
    public async Task RulesInInfoExcludeAreAcceptedAsync()
    {
        CommitIgnore("");
        File.WriteAllText(At(".git", "info", "exclude"), IsolatedRepo.MachineryIgnoreRules);

        Assert.Empty(await ReadyAsync());
    }

    [Fact]
    public async Task RulesInTheGlobalExcludesFileAreAcceptedAsync()
    {
        CommitIgnore("");
        string global = Path.Combine(repo.Home, "global-excludes");
        File.WriteAllText(global, IsolatedRepo.MachineryIgnoreRules);
        repo.Git("config", "core.excludesFile", global);

        Assert.Empty(await ReadyAsync());
    }

    // ---- harness configs `claustrum init` writes --------------------------------------------------------------

    [Theory]
    [InlineData(".mcp.json")]
    [InlineData("opencode.json")]
    [InlineData("opencode.jsonc")]
    public async Task AnUntrackedHarnessConfigIsRefusedWithTheWayOutNamedAsync(string file)
    {
        File.WriteAllText(At(file), "{}\n");

        string message = await RefusalAsync();

        Assert.Contains($"commit or git-ignore {file} (untracked) — an ignored local copy is fine", message, StringComparison.Ordinal);
        Assert.DoesNotContain("commit (or un-ignore)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredHarnessConfigIsTheOperatorsLocalCopyAndPassesWithoutAWarningAsync()
    {
        CommitIgnore(IsolatedRepo.MachineryIgnoreRules + ".mcp.json\n");
        File.WriteAllText(At(".mcp.json"), "{}\n");

        Assert.Empty(await ReadyAsync());
    }

    [Fact]
    public async Task AHarnessConfigStagedAsNewIsRefusedBecauseHeadHasNoCopyAsync()
    {
        File.WriteAllText(At(".mcp.json"), "{}\n");
        repo.Git("add", ".mcp.json");

        Assert.Contains("commit or git-ignore .mcp.json (uncommitted changes) — an ignored local copy is fine", await RefusalAsync(), StringComparison.Ordinal);
    }

    // G1: the children get HEAD's copy, which is what a committed config is for — a warning, not a refusal.
    [Theory]
    [InlineData(".mcp.json")]
    [InlineData("opencode.json")]
    public async Task ATrackedHarnessConfigWithALocalEditWarnsAndDoesNotRefuseAsync(string file)
    {
        File.WriteAllText(At(file), "{}\n");
        repo.CommitAll("harness config");
        File.WriteAllText(At(file), /*lang=json,strict*/ """{"mine":true}""" + "\n");

        string[] warnings = await ReadyAsync();

        Assert.Equal([$"{file} (uncommitted changes): the architect's worktree gets HEAD's copy — your local edit stays out of it"], warnings);
    }

    [Theory]
    [InlineData("--skip-worktree", "skip-worktree")]
    [InlineData("--assume-unchanged", "assume-unchanged")]
    public async Task ATrackedHarnessConfigHiddenByAnIndexBitWarnsNamingTheBitAsync(string flag, string state)
    {
        File.WriteAllText(At(".mcp.json"), "{}\n");
        repo.CommitAll("harness config");
        repo.Git("update-index", flag, ".mcp.json");
        File.WriteAllText(At(".mcp.json"), /*lang=json,strict*/ """{"token":"secret"}""" + "\n");

        string[] warnings = await ReadyAsync();

        Assert.Equal([$".mcp.json ({state}): the architect's worktree gets HEAD's copy — your local edit stays out of it"], warnings);
    }

    // ---- the cwd, git itself, and how the problems combine ----------------------------------------------------

    [Fact]
    public async Task ASubdirectoryCwdIsRefusedNamingTheRepositoryRootAsync()
    {
        string sub = At("sub");
        Directory.CreateDirectory(sub);

        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(() => ReadyAsync(cwd: sub));

        Assert.Equal(
            $"coordinate runs the architect in a worktree of the whole repository, whose children read the cast from its root: run it from {repo.Repo} (or --cwd \"{repo.Repo}\"), with the cast in .claustrum/casts/ there",
            refusal.Message);
    }

    [Fact]
    public async Task ARootCwdWithATrailingSeparatorIsTheRootAsync()
    {
        Assert.Empty(await ReadyAsync(cwd: repo.Repo + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task EveryProblemIsReportedAtOnceJoinedBySemicolonsAsync()
    {
        CommitIgnore("");
        File.AppendAllText(At("claustrum.json"), "\n");
        File.WriteAllText(At(".gitignore"), IsolatedRepo.MachineryIgnoreRules);
        File.WriteAllText(At("opencode.json"), "{}\n");

        string message = await RefusalAsync();

        Assert.StartsWith("coordinate runs the architect in a worktree, which sees only committed files — commit (or un-ignore) claustrum.json (uncommitted changes) first; .gitignore (uncommitted changes", message, StringComparison.Ordinal);
        Assert.Contains("; commit or git-ignore opencode.json (untracked) — an ignored local copy is fine", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommittedFilesAreReportedBeforeTheMissingRulesAsync()
    {
        CommitIgnore("");
        File.AppendAllText(At("claustrum.json"), "\n");

        string message = await RefusalAsync();

        int files = message.IndexOf("commit (or un-ignore) claustrum.json", StringComparison.Ordinal);
        int rules = message.IndexOf("git-ignore .claustrum/worktrees/", StringComparison.Ordinal);
        Assert.True(files >= 0 && rules > files, message);
        Assert.Contains("; coordinate runs the architect in a worktree and commits what it leaves there — git-ignore", message, StringComparison.Ordinal);
    }

    // A `.git` that is not a repository would fail the worktree add after the mint and leave a job nothing closes:
    // refused now, exit 2, like a failed `gh`.
    [Fact]
    public async Task AGitThatFailsIsRefusedAsync()
    {
        using ClaustrumCli broken = new();
        broken.MarkAsGitRoot();

        CliUsageException refusal = await Assert.ThrowsAsync<CliUsageException>(
            () => ArchitectWorktree.RequireReadyAsync(broken.Cwd, broken.Cwd, "default", Ct));

        Assert.StartsWith($"coordinate runs the architect in a git worktree, and `git check-ignore` failed in {broken.Cwd}: ", refusal.Message, StringComparison.Ordinal);
    }

    // ---- the stray probe `jobs clean` removes (G5) ------------------------------------------------------------

    [Fact]
    public async Task AnEmptyUnregisteredProbeDirectoryIsRemovedAsync()
    {
        string probe = At(".claustrum", "worktrees", "probe-0badf00d");
        Directory.CreateDirectory(probe);

        Assert.True(await ArchitectWorktree.TryRemoveStrayProbeAsync(repo.Repo, probe, Ct));

        Assert.False(Directory.Exists(probe));
    }

    [Fact]
    public async Task ANonEmptyProbeDirectoryIsLeftAloneAsync()
    {
        string probe = At(".claustrum", "worktrees", "probe-0badf00d");
        Directory.CreateDirectory(probe);
        File.WriteAllText(Path.Combine(probe, "kept.txt"), "x");

        Assert.False(await ArchitectWorktree.TryRemoveStrayProbeAsync(repo.Repo, probe, Ct));

        Assert.True(File.Exists(Path.Combine(probe, "kept.txt")));
    }

    [Fact]
    public async Task AnEmptyDirectoryNotNamedProbeIsLeftAloneAsync()
    {
        string other = At(".claustrum", "worktrees", "job-1");
        Directory.CreateDirectory(other);

        Assert.False(await ArchitectWorktree.TryRemoveStrayProbeAsync(repo.Repo, other, Ct));

        Assert.True(Directory.Exists(other));
    }

    // A registered worktree named `probe-…` whose files were emptied by hand: git lists it (prunable), so it is not a stray.
    [Fact]
    public async Task ARegisteredWorktreeNamedLikeAProbeIsLeftAloneEvenWhenEmptiedAsync()
    {
        string registered = At(".claustrum", "worktrees", "probe-feedface");
        repo.Git("worktree", "add", "-q", registered, "-b", "claustrum/probe-feedface");
        TempTree.Delete(registered);
        Directory.CreateDirectory(registered);

        Assert.False(await ArchitectWorktree.TryRemoveStrayProbeAsync(repo.Repo, registered, Ct));

        Assert.True(Directory.Exists(registered));
    }
}
