using Claustrum.Casts;
using Claustrum.Coordination;
using Claustrum.Roles;

namespace Claustrum.Tests.Coordination;

// The trap NOTES.md "`budget_exceeded` is four messages" names, applied to #61-#63: what an architect is told
// about an isolated builder's receipt lives in two copies — roles/architect/ROLE.md (the host architect's
// role) and CoordinationBrief.RenderSystemAppendix (a spawned architect's `## Coordination`) — and a reworded
// one in only one of them gives the two architects different instructions. Each fragment below must appear,
// verbatim after whitespace is flattened, in both texts.
public sealed class CoordinationTextAgreementTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-agreement-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private string ArchitectRole()
    {
        RoleLibrary library = new();
        return Flatten(new RoleRenderer(library).Render("architect", "high", "claude", cwd).SystemBody);
    }

    // Isolated, because that is the text with the git block in it: not isolated (no git repository) the appendix
    // is one line, and none of the shared fragments below belongs to it (#74 G4, asserted at the end of this file).
    private static string Appendix(bool isolated = true) => Flatten(CoordinationBrief.RenderSystemAppendix(
        new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"), [], BudgetUsd: null), "default", "/repo", isolated: isolated));

    public static TheoryData<string> SharedFragments =>
    [
        // #61: the commit on the receipt, and what can keep the work off the branch — four `work left uncommitted`
        // shapes (#74 round 4 added the mid-operation one, round 5 the unresolved conflicts) — each with its remedy.
        "its `warnings[]` says what did not land, in one of these shapes.",
        "`work left uncommitted on <branch>`: git refused that commit, and the work is still in the builder's `worktree`; commit it inside that worktree yourself before integrating.",
        "`work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not <branch>`: the builder stopped mid-rebase or switched branch, and a commit there would land off its branch — first finish or abort the rebase (`git -C <path> rebase --continue`/`--abort`) or `git -C <path> switch <branch>`, then commit inside it.",
        "`work left uncommitted: <path> has a <merge | rebase | git am> in progress, not a clean <branch>`, or `work left uncommitted: <path> has unresolved conflicts on <branch> — …`: the builder stopped mid-operation, and a commit there would conclude it — abort a merge (`git -C <path> merge --abort`: integration never makes a merge commit); finish or abort a rebase or `git am` (`--continue`/`--abort`); resolve the conflicts (fix the files, then `git -C <path> add` them) or abort what made them (`cherry-pick --abort`, `revert --abort`, `reset --merge`); then commit inside that worktree yourself.",
        "`work left uncommitted: <path> is not the job worktree on <branch> (…)`: the worktree was gone or foreign, so nothing reached the branch and what the builder wrote is in `<path>`, outside every branch — move it by hand or rerun the builder.",

        // #74 round 5 J5: a path git left out of the commit is routed too, or an architect integrates a branch missing it.
        "A `… left out of the commit on <branch>` warning means the branch lacks that path while the builder's worktree still holds it — if it belongs in the change, stage and commit it there yourself before integrating (an embedded repository: move it out or add it as a submodule first).",

        // T4: remediation is for an isolated builder, and names the branch on its receipt.
        "an in-place builder has no branch of its own — its work is already in your working tree",
        "one that ran under `max_parallel` above 1 or with `--branch`",
        "`--branch <the branch on that builder's receipt>`",

        // #74 F5: one integration order — rebase inside the builder's worktree, fast-forward from your own,
        // never rebase in your own, `jobs clean --cwd …` only once integrated. The deny clause is the appendix's alone.
        "git will not rebase a branch checked out elsewhere: rebase it inside the builder's worktree (`git -C <worktree> rebase <work branch>`), then fast-forward the work branch to it from your own working tree (`git merge --ff-only <builder branch>`).",
        "`git -C <worktree> rebase <work branch>`",
        "`git merge --ff-only <builder branch>`",
        "Never rebase in your own working tree: that leaves it on the builder's branch",
        "Once the builders are integrated — not before — run `claustrum jobs clean --cwd",
        "(finished worktrees go, branches stay)",

        // #58: the cap holds at every value.
        "at every value, 1 included",

        // #90: a builder past the cap waits, so running them at once is a matter of starting them at once — the recipe
        // lives in the Delegation contract, and both texts point at it with one sentence.
        "to actually run builders at once, start the runs in the background and `wait` (the recipe is in your Delegation contract)",
    ];

    [Theory]
    [MemberData(nameof(SharedFragments))]
    public void TheArchitectRoleAndTheCoordinationAppendixCarryTheSameInstruction(string fragment)
    {
        Assert.Contains(fragment, ArchitectRole(), StringComparison.Ordinal);
        Assert.Contains(fragment, Appendix(), StringComparison.Ordinal);
    }

    // Neither text may still tell an architect to name claustrum/<that job id>: a builder that ran with
    // --branch reports the branch it was given, and its own id names no branch.
    [Fact]
    public void NeitherTextStillNamesTheBuildersJobIdAsTheBranchToSendItBackTo()
    {
        Assert.DoesNotContain("--branch claustrum/<that job id>", ArchitectRole(), StringComparison.Ordinal);
        Assert.DoesNotContain("--branch claustrum/<that job id>", Appendix(), StringComparison.Ordinal);
    }

    // The architect role used to say "nothing to commit on its behalf" unconditionally (F3).
    [Fact]
    public void TheArchitectRoleQualifiesItsNothingToCommitClaimWithTheWarningException()
    {
        string role = ArchitectRole();
        int claim = role.IndexOf("so there is nothing to commit on its behalf", StringComparison.Ordinal);

        Assert.True(claim >= 0);
        Assert.StartsWith("— unless its `warnings[]` says what did not land", role[(claim + "so there is nothing to commit on its behalf".Length)..].TrimStart(), StringComparison.Ordinal);
    }

    // The deny clause is true only of a spawned, isolated architect (`git checkout`/`git switch` are denied for an
    // isolated run, #62); the host architect's ROLE.md must not claim it, and neither may the in-place appendix.
    [Fact]
    public void TheDenyClauseIsInTheIsolatedAppendixOnly()
    {
        const string deny = "`git checkout`/`git switch` are denied for this run";

        Assert.Contains(deny, Appendix(), StringComparison.Ordinal);
        Assert.DoesNotContain(deny, ArchitectRole(), StringComparison.Ordinal);
        Assert.DoesNotContain(deny, Appendix(isolated: false), StringComparison.Ordinal);
    }

    // The old order ("jobs clean first, then rebase") is wrong from an isolated architect's own worktree (#74 F5);
    // neither copy may still offer it.
    [Fact]
    public void NeitherTextStillOffersJobsCleanFirstOrRebasingFromTheArchitectsOwnTree()
    {
        string[] texts = [ArchitectRole(), Appendix()];
        foreach (string text in texts)
        {
            Assert.DoesNotContain("run `claustrum jobs clean` first", text, StringComparison.Ordinal);
            Assert.DoesNotContain("` first (finished worktrees go", text, StringComparison.Ordinal);
            Assert.DoesNotContain("or rebase inside that worktree", text, StringComparison.Ordinal);
        }
    }

    // The role ships to every user and devkit is private: a pointer into it is one most readers cannot follow
    // (#80 J6). The receipt for the review rule is Claustrum's own NOTES.md.
    [Fact]
    public void TheArchitectRoleHasNoPointerIntoTheTeamsPrivateDevkit()
    {
        Assert.DoesNotContain("Long form in the team's devkit", ArchitectRole(), StringComparison.Ordinal);
        Assert.DoesNotContain("devkit", ArchitectRole(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Receipt: Claustrum's own `NOTES.md` (orlodax/Claustrum #80).", ArchitectRole(), StringComparison.Ordinal);
    }

    // #74 G4: not isolated, the appendix has no git block at all, so a fragment of one that crept back would tell
    // an architect with no repository to rebase, clean and cap — and agree with the role by accident.
    [Theory]
    [MemberData(nameof(SharedFragments))]
    public void TheInPlaceAppendixCarriesNoneOfTheGitInstructionsTheRoleAndTheIsolatedAppendixShare(string fragment)
    {
        Assert.DoesNotContain(fragment, Appendix(isolated: false), StringComparison.Ordinal);
    }
}
