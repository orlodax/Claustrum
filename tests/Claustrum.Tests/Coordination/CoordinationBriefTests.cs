using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Delegation;

namespace Claustrum.Tests.Coordination;

// CoordinationBrief is pure and dependency-free by design (its own class comment) — no gh, no
// backend, no job on disk — so every case here is a hand-built Cast against RenderSystemAppendix/
// RenderUserPrompt directly.
public sealed class CoordinationBriefTests
{
    private static Cast BuildCast(
        CastArchitect? architect = null,
        Dictionary<string, CastRoleEntry?>? roles = null,
        decimal? budgetUsd = null) =>
        new("default", "1.0.0", architect ?? new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"), roles ?? [], budgetUsd);

    [Fact]
    public void ARoleTheCastMarksNullReadsAsNotNeeded()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?> { ["tester"] = null });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("- tester: not needed for this cast — do not delegate to it", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyBuilderCarriesMaxParallelInItsLine()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null, MaxParallel: 3),
            ["tester"] = new CastRoleEntry(Model: "claude:haiku", Backend: null, Tier: null),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("- builder: model claude:opus, tier high, max_parallel 3", appendix, StringComparison.Ordinal);
        Assert.Contains("- tester: model claude:haiku, tier high", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("tester: model claude:haiku, tier high, max_parallel", appendix, StringComparison.Ordinal);
    }

    // review R6 (2026-10-08): a null entry used to print `max_parallel 1`, right under "a cap at every value,
    // 1 included" — but a builder entry with no value is not gated at all since #58 (every hand-written cast
    // without the key, every questionnaire cast made before F1). It says so.
    [Fact]
    public void BuilderWithNoMaxParallelSetSaysItHasNoCapAndNoIsolation()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains(
            """- builder: model per claustrum.json, tier high, max_parallel not set (no cap, no isolation — add "max_parallel": 1 to the cast to serialise builders)""",
            appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("max_parallel 1", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderWithMaxParallelOnePrintsMaxParallelOne()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null, MaxParallel: 1),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("- builder: model per claustrum.json, tier high, max_parallel 1\n", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("max_parallel not set", appendix, StringComparison.Ordinal);
    }

    // A hand-edited value below 1 is no cap either, and prints as its number (CoordinationBrief.RoleLine's ⚠).
    [Fact]
    public void ABuilderMaxParallelBelowOnePrintsAsItsNumber()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null, MaxParallel: 0),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("tier high, max_parallel 0\n", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void ACappedCastPrintsTheBudgetLine()
    {
        Cast cast = BuildCast(budgetUsd: 12.5m);

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("Budget: $12.50 across the whole job tree", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlimitedCastPrintsTheUnlimitedLineInstead()
    {
        Cast cast = BuildCast(budgetUsd: null);

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("Budget: unlimited — children are not accounted", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("across the whole job tree", appendix, StringComparison.Ordinal);
    }

    // The fixed pipeline order (builder, code-reviewer, ui-reviewer, tester) first, then any other
    // cast role alphabetically — CoordinationBrief.pipelineOrder / IsExtraRole.
    [Fact]
    public void RolesRenderInPipelineOrderThenExtrasAlphabetically()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["tester"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null),
            ["builder"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null),
            ["zebra-role"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null),
            ["alpha-role"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null),
            ["code-reviewer"] = new CastRoleEntry(Model: "claude:opus", Backend: null, Tier: null),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        int builder = appendix.IndexOf("- builder:", StringComparison.Ordinal);
        int codeReviewer = appendix.IndexOf("- code-reviewer:", StringComparison.Ordinal);
        int tester = appendix.IndexOf("- tester:", StringComparison.Ordinal);
        int alpha = appendix.IndexOf("- alpha-role:", StringComparison.Ordinal);
        int zebra = appendix.IndexOf("- zebra-role:", StringComparison.Ordinal);

        Assert.True(builder < codeReviewer, "builder must come before code-reviewer");
        Assert.True(codeReviewer < tester, "code-reviewer must come before tester (ui-reviewer absent here)");
        Assert.True(tester < alpha, "the fixed pipeline order must come before any extra role");
        Assert.True(alpha < zebra, "extra roles must be alphabetical");
    }

    [Fact]
    public void CwdAndCastNameAreQuotedInTheDelegateCommand()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "my cast", "/repo with space");

        Assert.Contains(
            "Delegate with: claustrum run <role> --cast \"my cast\" --brief-file <path> --json --cwd \"/repo with space\"",
            appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModelOverrideOmitsTheThisRunSuffix()
    {
        Cast cast = BuildCast(architect: new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"));

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", modelOverride: null);

        Assert.Contains("- architect: mode spawned, model claude:opus, tier high — that is you, in this run", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("this run:", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelOverrideAddsTheThisRunSuffixNextToTheCastsOwnModel()
    {
        Cast cast = BuildCast(architect: new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"));

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", modelOverride: "claude:haiku");

        Assert.Contains("model claude:opus, tier high (this run: claude:haiku)", appendix, StringComparison.Ordinal);
    }

    // #58, #61, #62, #63 as the architect is told them. Every line below is an instruction a spawned
    // architect obeys literally, so a reworded one is a behaviour change: the texts are pinned whole.
    [Fact]
    public void TheAppendixSaysAnIsolatedBuildersBranchCarriesItsWorkAsACommitWithThreeWarningShapes()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo");

        Assert.Contains("and that branch carries its work as a commit (`commit` on the receipt) — unless its `warnings[]` says the work was left uncommitted, in one of three shapes.", appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted on <branch>`: git refused that commit, and the work is still in the builder's `worktree`; commit it inside that worktree yourself before integrating.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not <branch>`: the builder stopped mid-rebase or switched branch, and a commit there would land off its branch — first finish or abort the rebase (`git -C <path> rebase --continue`/`--abort`) or `git -C <path> switch <branch>`, then commit inside it.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted: <path> is not the job worktree on <branch> (…)`: the worktree was gone or foreign, so nothing reached the branch and what the builder wrote is in `<path>`, outside every branch — move it by hand or rerun the builder.",
            appendix, StringComparison.Ordinal);
    }

    // review F5: a builder's branch stays checked out in its worktree until `jobs clean`, and git will not
    // rebase a branch checked out elsewhere (exit 128, measured), so integration frees it first.
    [Fact]
    public void TheAppendixSaysToCleanFinishedWorktreesBeforeRebasingOrToRebaseInsideTheWorktree()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo with space");

        Assert.Contains("Integrate each builder branch by rebasing it onto the work branch, then fast-forward the work branch to it.", appendix, StringComparison.Ordinal);
        Assert.Contains("git will not rebase a branch checked out elsewhere", appendix, StringComparison.Ordinal);
        Assert.Contains("run `claustrum jobs clean --cwd \"/repo with space\"` first (finished worktrees go, branches stay)", appendix, StringComparison.Ordinal);
        Assert.Contains("or rebase inside that worktree (`git -C <worktree> rebase <work branch>`)", appendix, StringComparison.Ordinal);
        Assert.Contains("- Never a merge commit, never `git push`.", appendix, StringComparison.Ordinal);
    }

    // review T4: the remediation hint is for an isolated builder; an in-place one has no branch of its own, and
    // the branch to name is the one on that builder's receipt, not claustrum/<its job id> (a builder that itself
    // ran with --branch reports the branch it was given, and its own id names none).
    [Fact]
    public void TheAppendixSendsAnIsolatedBuildersBranchBackWithTheBranchOnItsReceipt()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo");

        Assert.Contains("To send an isolated builder's reviewed branch back for remediation", appendix, StringComparison.Ordinal);
        Assert.Contains("(one that ran under `max_parallel` above 1 or with `--branch`; an in-place builder has no branch of its own — its work is already in your working tree)", appendix, StringComparison.Ordinal);
        Assert.Contains("run the builder with `--branch <the branch on that builder's receipt>`", appendix, StringComparison.Ordinal);
        Assert.Contains("it continues on the same branch, in a fresh worktree, and its fix lands there as another commit.", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("--branch claustrum/<", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppendixSaysMaxParallelIsACapAtEveryValueOneIncluded()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo");

        Assert.Contains("- `max_parallel` is a cap at every value, 1 included: a builder past it waits for a slot. Isolation starts at 2.", appendix, StringComparison.Ordinal);
    }

    // The job id does not exist yet when this renders (issue #23): the three lines that used to
    // name it carry DelegateRequest.JobIdToken instead, substituted later by
    // PreparedDelegation.ForJob once a job directory is minted (PreparedDelegationTests).
    [Fact]
    public void TheJobIdTokenAndInspectCommandAppear()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains($"Job tree: {DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
        Assert.Contains($"Inspect: claustrum jobs budget {DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
        Assert.Contains($"Work branch: claustrum/{DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
    }

    // The four distinct shapes a budget_exceeded error can take (DelegateEngine/BudgetLedger), all
    // named so a spawned architect knows what to do for each without re-deriving it from an error string.
    [Fact]
    public void TheFourBudgetExceededShapesAreAllNamed()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo");

        Assert.Contains("while N running job(s) hold", appendix, StringComparison.Ordinal);
        Assert.Contains("$R remaining; --budget X exceeds it", appendix, StringComparison.Ordinal);
        Assert.Contains("rounds to $0.00 — pass --budget", appendix, StringComparison.Ordinal);
        Assert.Contains("$0.00 remaining", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void UserPromptEndsWithTheCoordinationPointer()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("do the thing", [], "/repo");

        Assert.EndsWith(
            "Your system prompt carries a `## Coordination` section — the cast, the delegate command, the budget rules and your work branch. Follow it literally.",
            prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void NoIssuesOmitsTheClosesBullet()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("do the thing", [], "/repo");

        Assert.DoesNotContain("Closes #", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Issues:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void IssuesAddTheClosesBulletNamingEveryNumber()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("do the thing", [12, 13], "/repo");

        Assert.Contains("Issues: #12, #13.", prompt, StringComparison.Ordinal);
        Assert.Contains("`Closes #<n>`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UserPromptCarriesTheTaskAndTheWorkingDirectory()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("fix the bug", [], "/some/repo");

        Assert.Contains("## Task\nfix the bug", prompt, StringComparison.Ordinal);
        Assert.Contains("- Working directory: /some/repo", prompt, StringComparison.Ordinal);
    }
}
