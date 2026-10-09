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
    public void TheAppendixSaysAnIsolatedBuildersBranchCarriesItsWorkAsACommitAndWhatDidNotLandInFourShapes()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo", isolated: true);

        Assert.Contains("and that branch carries its work as a commit (`commit` on the receipt) — unless its `warnings[]` says what did not land, in one of these shapes.", appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted on <branch>`: git refused that commit, and the work is still in the builder's `worktree`; commit it inside that worktree yourself before integrating.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not <branch>`: the builder stopped mid-rebase or switched branch, and a commit there would land off its branch — first finish or abort the rebase (`git -C <path> rebase --continue`/`--abort`) or `git -C <path> switch <branch>`, then commit inside it.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted: <path> has a <merge | rebase | git am> in progress, not a clean <branch>`, or `work left uncommitted: <path> has unresolved conflicts on <branch> — …`: the builder stopped mid-operation, and a commit there would conclude it — abort a merge (`git -C <path> merge --abort`: integration never makes a merge commit); finish or abort a rebase or `git am` (`--continue`/`--abort`); resolve the conflicts (fix the files, then `git -C <path> add` them) or abort what made them (`cherry-pick --abort`, `revert --abort`, `reset --merge`); then commit inside that worktree yourself.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "`work left uncommitted: <path> is not the job worktree on <branch> (…)`: the worktree was gone or foreign, so nothing reached the branch and what the builder wrote is in `<path>`, outside every branch — move it by hand or rerun the builder.",
            appendix, StringComparison.Ordinal);
        Assert.Contains(
            "A `… left out of the commit on <branch>` warning means the branch lacks that path while the builder's worktree still holds it — if it belongs in the change, stage and commit it there yourself before integrating (an embedded repository: move it out or add it as a submodule first).",
            appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("in one of three shapes", appendix, StringComparison.Ordinal);
    }

    // #74 F5: a builder's branch stays checked out in its worktree until `jobs clean`, and git will not rebase a
    // branch checked out elsewhere (exit 128, measured). From an isolated architect's own worktree `git rebase
    // <work> <builder branch>` also leaves it on the builder's branch, and `git checkout`/`git switch` are denied
    // (#62), so the one order is: rebase inside the builder's worktree, fast-forward from your own, clean last.
    [Fact]
    public void TheAppendixSaysToRebaseInsideTheBuildersWorktreeFastForwardFromYourOwnAndCleanOnlyOnceIntegrated()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo with space", isolated: true);
        string worktree = Path.Combine("/repo with space", ".claustrum", "worktrees", DelegateRequest.JobIdToken);

        Assert.Contains("Integrate each builder branch by rebasing it onto the work branch, then fast-forward the work branch to it.", appendix, StringComparison.Ordinal);
        Assert.Contains("git will not rebase a branch checked out elsewhere: rebase it inside the builder's worktree (`git -C <worktree> rebase <work branch>`), then fast-forward the work branch to it from your own working tree (`git merge --ff-only <builder branch>`).", appendix, StringComparison.Ordinal);
        Assert.Contains("Never rebase in your own working tree: that leaves it on the builder's branch, and `git checkout`/`git switch` are denied for this run.", appendix, StringComparison.Ordinal);
        Assert.Contains(
            $"Once the builders are integrated — not before — run `claustrum jobs clean --cwd \"{worktree}\"` (finished worktrees go, branches stay).",
            appendix, StringComparison.Ordinal);
        Assert.Contains("- Never a merge commit, never `git push`.", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("run `claustrum jobs clean --cwd \"/repo with space\"` first", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("or rebase inside that worktree", appendix, StringComparison.Ordinal);
    }

    // review T4: the remediation hint is for an isolated builder; an in-place one has no branch of its own, and
    // the branch to name is the one on that builder's receipt, not claustrum/<its job id> (a builder that itself
    // ran with --branch reports the branch it was given, and its own id names none).
    [Fact]
    public void TheAppendixSendsAnIsolatedBuildersBranchBackWithTheBranchOnItsReceipt()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo", isolated: true);

        Assert.Contains("To send an isolated builder's reviewed branch back for remediation", appendix, StringComparison.Ordinal);
        Assert.Contains("(one that ran under `max_parallel` above 1 or with `--branch`; an in-place builder has no branch of its own — its work is already in your working tree)", appendix, StringComparison.Ordinal);
        Assert.Contains("run the builder with `--branch <the branch on that builder's receipt>`", appendix, StringComparison.Ordinal);
        Assert.Contains("it continues on the same branch, in a fresh worktree, and its fix lands there as another commit.", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("--branch claustrum/<", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppendixSaysMaxParallelIsACapAtEveryValueOneIncluded()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo", isolated: true);

        Assert.Contains("- `max_parallel` is a cap at every value, 1 included: a builder past it waits for a slot — to actually run builders at once, start the runs in the background and `wait` (the recipe is in your Delegation contract). Isolation starts at 2.", appendix, StringComparison.Ordinal);
    }

    // The job id does not exist yet when this renders (issue #23): the three lines that used to
    // name it carry DelegateRequest.JobIdToken instead, substituted later by
    // PreparedDelegation.ForJob once a job directory is minted (PreparedDelegationTests).
    [Fact]
    public void TheJobIdTokenAndInspectCommandAppear()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", isolated: true);

        Assert.Contains($"Job tree: {DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
        Assert.Contains($"Inspect: claustrum jobs budget {DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
        Assert.Contains($"Work branch: claustrum/{DelegateRequest.JobIdToken}", appendix, StringComparison.Ordinal);
    }

    // #74: isolated, every path the architect hands a child is its own worktree's — the `Delegate with:` line,
    // the `Work branch:` block's own location and the final `jobs clean --cwd` — never the operator's cwd.
    [Fact]
    public void AnIsolatedAppendixNamesTheArchitectsWorktreeOnTheDelegateLineTheWorkBranchAndTheCleanCommand()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "my cast", "/repo with space", isolated: true);
        string worktree = Path.Combine("/repo with space", ".claustrum", "worktrees", DelegateRequest.JobIdToken);

        Assert.Contains($"Delegate with: claustrum run <role> --cast \"my cast\" --brief-file <path> --json --cwd \"{worktree}\"", appendix, StringComparison.Ordinal);
        Assert.Contains($"Work branch: claustrum/{DelegateRequest.JobIdToken} — you are already on it, in your own worktree {worktree}, cut from the operator's HEAD: it holds committed files only.", appendix, StringComparison.Ordinal);
        Assert.Contains("- The main checkout at /repo with space is the operator's: never cd into it, and never change its branch or its files.", appendix, StringComparison.Ordinal);
        Assert.Contains($"- Every delegation takes --cwd \"{worktree}\", as `Delegate with:` above does: an isolated builder's worktree then nests under yours, cut from the work branch's tip.", appendix, StringComparison.Ordinal);
        Assert.Contains("- Commit your integration yourself and leave your worktree clean: when you finish, the runner commits whatever is still uncommitted in it onto the work branch, stray files included.", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("--cwd \"/repo with space\"", appendix, StringComparison.Ordinal);
    }

    // #74 G4: in place means no git repository, so no git line may survive — every one would be an instruction
    // the architect cannot carry out (a work branch to be on, a rebase, a worktree to clean).
    [Fact]
    public void AnInPlaceAppendixSaysThereIsNoGitRepositoryInOneLineAndKeepsNoGitInstruction()
    {
        string appendix = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/plain dir");

        Assert.Contains(
            "No git repository at /plain dir: there is no work branch and no worktree isolation — builders run in place, one at a time, and their changes land directly in /plain dir; nothing to rebase or clean.",
            appendix, StringComparison.Ordinal);
        Assert.Contains("Delegate with: claustrum run <role> --cast \"default\" --brief-file <path> --json --cwd \"/plain dir\"", appendix, StringComparison.Ordinal);
        Assert.EndsWith("nothing to rebase or clean.", appendix, StringComparison.Ordinal);
        string[] gone =
        [
            "Work branch", "rebase <work", "git rebase", "jobs clean", "--branch <", "Never a merge commit", "never `git push`",
            "cap at every value", "at every value", "Isolation starts at 2", "git merge --ff-only", "left uncommitted", "left out of the commit",
            "your own worktree", "operator's HEAD", "worktrees",
        ];
        foreach (string fragment in gone)
            Assert.DoesNotContain(fragment, appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultRenderIsTheInPlaceOneAndIsolatedFalseSaysTheSame()
    {
        string byDefault = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo");
        string explicitFalse = CoordinationBrief.RenderSystemAppendix(BuildCast(), "default", "/repo", isolated: false);

        Assert.Equal(explicitFalse, byDefault);
        Assert.Contains("No git repository at /repo:", byDefault, StringComparison.Ordinal);
    }

    // The two renders differ only in the git block after the briefs line: everything above it — the cast, the
    // budget lines, the token lines, the budget_exceeded shapes — is one text.
    [Fact]
    public void TheIsolatedAndInPlaceAppendicesShareEverythingAboveTheGitBlock()
    {
        string inPlace = CoordinationBrief.RenderSystemAppendix(BuildCast(budgetUsd: 4m), "default", "/repo");
        string isolated = CoordinationBrief.RenderSystemAppendix(BuildCast(budgetUsd: 4m), "default", "/repo", isolated: true);
        const string briefs = "- Write each brief to .claustrum/briefs/<n>-<role>.md first, then pass that path to --brief-file.\n";
        string worktree = Path.Combine("/repo", ".claustrum", "worktrees", DelegateRequest.JobIdToken);
        string sharedHead = inPlace[..(inPlace.IndexOf(briefs, StringComparison.Ordinal) + briefs.Length)];

        // Only the `Delegate with:` --cwd differs above the git block: the worktree, not the operator's checkout.
        Assert.StartsWith(sharedHead.Replace("--cwd \"/repo\"", $"--cwd \"{worktree}\"", StringComparison.Ordinal), isolated, StringComparison.Ordinal);
        Assert.DoesNotContain("Work branch", inPlace, StringComparison.Ordinal);
        Assert.Contains("Work branch", isolated, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchitectCwdIsTheWorktreeOnlyWhenIsolated()
    {
        Assert.Equal("/repo", CoordinationBrief.ArchitectCwd("/repo", isolated: false));
        Assert.Equal(
            Path.Combine("/repo", ".claustrum", "worktrees", DelegateRequest.JobIdToken),
            CoordinationBrief.ArchitectCwd("/repo", isolated: true));
    }

    [Fact]
    public void WorkingDirectoryLineIsTheOneLineBindUserPromptReplaces()
    {
        Assert.Equal("- Working directory: /some/dir", CoordinationBrief.WorkingDirectoryLine("/some/dir"));
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

    // #89: the task above `## Context` is the caller's or an issue's text, and a brief about this very code quotes the
    // exact placeholder line; BindWorkingDirectory binds the line Claustrum wrote, in the block Claustrum wrote.
    private const string Placeholder = "/repo/.claustrum/worktrees/{{job_id}}";

    private const string Bound = "/repo/.claustrum/worktrees/20261009-120000-abcdef12";

    [Fact]
    public void BindWorkingDirectoryMovesTheContextLineFromThePlaceholderToTheRealPath()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("do the thing", [12, 13], Placeholder);

        string bound = CoordinationBrief.BindWorkingDirectory(prompt, Placeholder, Bound);

        Assert.Equal(prompt.Replace($"- Working directory: {Placeholder}", $"- Working directory: {Bound}", StringComparison.Ordinal), bound);
        Assert.Contains($"- Working directory: {Bound}", bound, StringComparison.Ordinal);
        Assert.DoesNotContain("{{job_id}}", bound, StringComparison.Ordinal);
    }

    [Fact]
    public void BindWorkingDirectoryLeavesATaskThatQuotesTheExactPlaceholderLineVerbatim()
    {
        string quoted = $"- Working directory: {Placeholder}";
        string task = $"the architect's brief should end with\n\n{quoted}\n\nand a second mention: `{{{{job_id}}}}`";
        string prompt = CoordinationBrief.RenderUserPrompt(task, [], Placeholder);

        string bound = CoordinationBrief.BindWorkingDirectory(prompt, Placeholder, Bound);

        int context = bound.LastIndexOf("\n## Context\n", StringComparison.Ordinal);
        Assert.Equal(task, bound["## Task\n".Length..context].Trim());
        Assert.Contains($"\n{quoted}\n\nand a second mention: `{{{{job_id}}}}`", bound[..context], StringComparison.Ordinal);
        Assert.Equal($"- Working directory: {Bound}", bound[context..].Split('\n').Single(line => line.StartsWith("- Working directory:", StringComparison.Ordinal)));
    }

    [Fact]
    public void BindWorkingDirectoryBindsOnlyTheRenderersBlockWhenTheTaskHasItsOwnContextHeading()
    {
        string quoted = $"- Working directory: {Placeholder}";
        string task = $"explain what this does:\n\n## Context\n{quoted}\n- Issues: none\n\nthen stop";
        string prompt = CoordinationBrief.RenderUserPrompt(task, [], Placeholder);

        string bound = CoordinationBrief.BindWorkingDirectory(prompt, Placeholder, Bound);

        Assert.StartsWith($"## Task\n{task}\n\n## Context\n", bound, StringComparison.Ordinal);
        Assert.Equal(2, bound.Split("\n## Context\n").Length - 1);
        Assert.Equal(1, bound.Split(quoted).Length - 1);
        Assert.Equal(1, bound.Split($"- Working directory: {Bound}").Length - 1);
        Assert.True(bound.IndexOf(quoted, StringComparison.Ordinal) < bound.LastIndexOf("\n## Context\n", StringComparison.Ordinal), "the surviving placeholder line is the task's");
    }

    [Fact]
    public void BindWorkingDirectoryOfATaskWhoseLastLineIsAContextHeadingStillFindsTheRenderersOne()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("see below\n## Context", [], Placeholder);

        string bound = CoordinationBrief.BindWorkingDirectory(prompt, Placeholder, Bound);

        Assert.StartsWith("## Task\nsee below\n## Context\n\n## Context\n", bound, StringComparison.Ordinal);
        Assert.Contains($"- Working directory: {Bound}", bound, StringComparison.Ordinal);
    }

    [Fact]
    public void BindWorkingDirectoryOfAPromptWithoutAContextHeadingThrowsInsteadOfBindingNothing()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => CoordinationBrief.BindWorkingDirectory($"## Task\nonly a task\n- Working directory: {Placeholder}", Placeholder, Bound));

        Assert.Contains("## Context", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## Context\n- Working directory: x")]
    [InlineData("## Task\nt\n## Contextual\n- Working directory: x")]
    [InlineData("")]
    public void BindWorkingDirectoryNeedsTheHeadingOnItsOwnLineAfterANewline(string notAPrompt)
    {
        Assert.Throws<InvalidOperationException>(() => CoordinationBrief.BindWorkingDirectory(notAPrompt, "x", "y"));
    }

    [Fact]
    public void BindWorkingDirectoryOfAContextBlockWithoutThePlaceholderChangesNothing()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("do the thing", [], "/somewhere/else");

        Assert.Equal(prompt, CoordinationBrief.BindWorkingDirectory(prompt, Placeholder, Bound));
    }

    [Fact]
    public void RenderUserPromptForANonIsolatedRunIsUnchangedByThisChange()
    {
        string prompt = CoordinationBrief.RenderUserPrompt("  do the thing\n", [12], "/repo");

        Assert.Equal(
            "## Task\ndo the thing\n\n## Context\n"
            + "- Issues: #12. The commit or PR that resolves one cites `Closes #<n>` (owner's rule).\n"
            + "- Working directory: /repo\n"
            + "- Your system prompt carries a `## Coordination` section — the cast, the delegate command, the budget rules and your work branch. Follow it literally.",
            prompt);
    }
}
