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

    private static string Appendix() => Flatten(CoordinationBrief.RenderSystemAppendix(
        new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"), [], BudgetUsd: null), "default", "/repo"));

    public static TheoryData<string> SharedFragments =>
    [
        // #61: the commit on the receipt, and the three ways the work is left uncommitted, each with its remedy.
        "its `warnings[]` says the work was left uncommitted, in one of three shapes.",
        "`work left uncommitted on <branch>`: git refused that commit, and the work is still in the builder's `worktree`; commit it inside that worktree yourself before integrating.",
        "`work left uncommitted: <path> is on <a detached HEAD | refs/heads/…>, not <branch>`: the builder stopped mid-rebase or switched branch, and a commit there would land off its branch — first finish or abort the rebase (`git -C <path> rebase --continue`/`--abort`) or `git -C <path> switch <branch>`, then commit inside it.",
        "`work left uncommitted: <path> is not the job worktree on <branch> (…)`: the worktree was gone or foreign, so nothing reached the branch and what the builder wrote is in `<path>`, outside every branch — move it by hand or rerun the builder.",

        // T4: remediation is for an isolated builder, and names the branch on its receipt.
        "an in-place builder has no branch of its own — its work is already in your working tree",
        "one that ran under `max_parallel` above 1 or with `--branch`",
        "`--branch <the branch on that builder's receipt>`",

        // F5: integration frees the branch first, or rebases inside the worktree.
        "git will not rebase a branch checked out elsewhere",
        "run `claustrum jobs clean",
        "(finished worktrees go, branches stay)",
        "`git -C <worktree> rebase <work branch>`",

        // #58: the cap holds at every value.
        "at every value, 1 included",
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
        Assert.StartsWith("— unless its `warnings[]` says the work was left uncommitted", role[(claim + "so there is nothing to commit on its behalf".Length)..].TrimStart(), StringComparison.Ordinal);
    }
}
