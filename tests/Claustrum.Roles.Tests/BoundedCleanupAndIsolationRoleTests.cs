namespace Claustrum.Roles.Tests;

// M4 wave 1's role text: the bounded-cleanup rule #64 added to the builder and the tester ("delete only what
// you created, by name" — on 2026-10-08 a tester's `rm -rf /tmp/tmp.*` deleted every `mktemp -d` on the
// machine), and what the architect is told about an isolated builder's receipt (#58, #61, #63). The architect
// wording has a second copy in CoordinationBrief.RenderSystemAppendix; Claustrum.Tests'
// CoordinationTextAgreementTests keeps the two in step, this pins the role's own side on every harness.
public sealed class BoundedCleanupAndIsolationRoleTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-bounded-cleanup-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private string Body(string role, string harness) =>
        Prose.Flatten(new RoleRenderer(library).Render(role, "high", harness, cwd).SystemBody);

    public static TheoryData<string, string> BuilderAndTesterOnEveryHarness => new()
    {
        { "builder", "claude" }, { "builder", "opencode" }, { "builder", "cursor" }, { "builder", "copilot" },
        { "tester", "claude" }, { "tester", "opencode" }, { "tester", "cursor" }, { "tester", "copilot" },
    };

    [Theory]
    [MemberData(nameof(BuilderAndTesterOnEveryHarness))]
    public void TheBuilderAndTesterDeleteOnlyWhatTheyCreatedByNameNeverAGlobOverASharedTempDirectory(string role, string harness)
    {
        string body = Body(role, harness);

        Assert.Equal(1, body.Split("**Delete only what you created, by name.**").Length - 1);
        Assert.Contains("never a glob over a shared temp directory", body, StringComparison.Ordinal);
        Assert.Contains("`rm -rf /tmp/tmp.*`", body, StringComparison.Ordinal);
        Assert.Contains("other agents' included", body, StringComparison.Ordinal);
    }

    // The rule sits inside the "Clean up what you start" section it bounds, not in some other part of the role.
    [Theory]
    [MemberData(nameof(BuilderAndTesterOnEveryHarness))]
    public void TheBoundedCleanupRuleIsPartOfTheCleanUpWhatYouStartSection(string role, string harness)
    {
        string body = Body(role, harness);
        int section = body.IndexOf("## Clean up what you start", StringComparison.Ordinal);
        int rule = body.IndexOf("**Delete only what you created, by name.**", StringComparison.Ordinal);
        int nextSection = body.IndexOf(" ## ", section + 1, StringComparison.Ordinal);

        Assert.True(section >= 0 && section < rule, "the rule must follow the section's heading");
        Assert.True(nextSection < 0 || rule < nextSection, "the rule must come before the next section starts");
    }

    [Fact]
    public void TheTestersRuleNamesTheIncidentThatMadeItNecessary()
    {
        string body = Body("tester", "claude");

        Assert.Contains("on 2026-10-08 a tester's `rm -rf /tmp/tmp.*` deleted every `mktemp -d` on the machine", body, StringComparison.Ordinal);
        Assert.Contains("Put scratch repos and files in one directory you made for this run and remove that one path", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildersRuleTellsItToKeepScratchFilesInOneDirectoryItMade()
    {
        string body = Body("builder", "claude");

        Assert.Contains("Keep scratch files in one directory you made for this run and remove that one path", body, StringComparison.Ordinal);
        Assert.Contains("matches every `mktemp -d` on the machine", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectOnEveryHarnessIsToldTheCapHoldsAtEveryValueAndWhatAnIsolatedReceiptCarries(string harness)
    {
        string body = Body("architect", harness);

        Assert.Contains("The cap holds at every value, 1 included: at 1 builders run one after another in your working tree; above 1 each works in its own git worktree on its own branch.", body, StringComparison.Ordinal);
        Assert.Contains("`changed_files`, and `worktree`/`branch`/`commit` for builders that ran isolated.", body, StringComparison.Ordinal);
        Assert.Contains("**An isolated builder's branch already carries its work** as a commit (`commit` on the receipt)", body, StringComparison.Ordinal);
        Assert.Contains("`--branch <the branch on that builder's receipt>` (`branch` on `delegate`)", body, StringComparison.Ordinal);
        // #74 F5: one integration order — rebase inside the builder's worktree, fast-forward from your own, clean once integrated.
        Assert.Contains("rebase it inside the builder's worktree (`git -C <worktree> rebase <work branch>`), then fast-forward the work branch to it from your own working tree (`git merge --ff-only <builder branch>`)", body, StringComparison.Ordinal);
        Assert.Contains("Never rebase in your own working tree: that leaves it on the builder's branch.", body, StringComparison.Ordinal);
        Assert.Contains("Once the builders are integrated — not before — run `claustrum jobs clean --cwd <your working tree>` (finished worktrees go, branches stay).", body, StringComparison.Ordinal);
        Assert.DoesNotContain("run `claustrum jobs clean` first", body, StringComparison.Ordinal);
    }

    // #74 rounds 4-5: what an architect is told the receipt can say besides a refused commit.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectOnEveryHarnessIsToldTheShapesOfWhatDidNotLand(string harness)
    {
        string body = Body("architect", harness);

        Assert.Contains("unless its `warnings[]` says what did not land, in one of these shapes.", body, StringComparison.Ordinal);
        Assert.Contains("`work left uncommitted: <path> has a <merge | rebase | git am> in progress, not a clean <branch>`, or `work left uncommitted: <path> has unresolved conflicts on <branch> — …`", body, StringComparison.Ordinal);
        Assert.Contains("(`git -C <path> merge --abort`: integration never makes a merge commit)", body, StringComparison.Ordinal);
        Assert.Contains("A `… left out of the commit on <branch>` warning means the branch lacks that path while the builder's worktree still holds it", body, StringComparison.Ordinal);
        Assert.Contains("(an embedded repository: move it out or add it as a submodule first)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("in one of three shapes", body, StringComparison.Ordinal);
    }

    // #80 J6: the receipt pointer names a public repository; ROLE.md ships to every user and devkit is private.
    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectRoleNamesNoPrivateDevkitPointer(string harness)
    {
        string body = Body("architect", harness);

        Assert.DoesNotContain("Long form in the team's devkit", body, StringComparison.Ordinal);
        Assert.DoesNotContain("devkit", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Receipt: Claustrum's own `NOTES.md` (orlodax/Claustrum #80).", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectDoesNotStillCallEveryBuilderParallelOrNameAJobIdAsTheBranchToSendBackTo(string harness)
    {
        string body = Body("architect", harness);

        Assert.DoesNotContain("`worktree`/`branch` for builders that ran in parallel", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Each parallel builder works in its own git worktree on its own branch.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("--branch claustrum/<that job id>", body, StringComparison.Ordinal);
    }
}
