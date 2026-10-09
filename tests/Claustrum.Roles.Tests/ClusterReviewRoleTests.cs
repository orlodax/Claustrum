namespace Claustrum.Roles.Tests;

// #80: devkit PR #12's rule, ported 2026-10-09 — blind review runs once per cluster of related changes,
// never once per builder or per fix, and one tester gate follows it. The wording is an instruction a spawned
// agent obeys literally, so each assertion is one sentence of the port, on every harness the role renders for.
// Phrases are matched on flattened prose (Prose.Flatten); the tier stubs carry none of this text.
public sealed class ClusterReviewRoleTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-cluster-review-").FullName;
    private readonly RoleLibrary library = new();

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private string Body(string role, string harness) =>
        Prose.Flatten(new RoleRenderer(library).Render(role, "high", harness, cwd).SystemBody);

    // The owner's block is a markdown quote: flattened, every wrapped line keeps its `> ` lead, which sits between words.
    private string QuotedBlockBody(string role, string harness) => Body(role, harness).Replace(" > ", " ", StringComparison.Ordinal);

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectCollectsTheWholeClusterThenReviewsPerSetOfRelatedChangesThenRunsOneTester(string harness)
    {
        string body = Body("architect", harness);

        Assert.Contains("4. **Collect the whole cluster.**", body, StringComparison.Ordinal);
        Assert.Contains("a review or a gate run against a half-built cluster judges a state that will never ship", body, StringComparison.Ordinal);
        Assert.Contains("5. **Blind review, per set of related changes.**", body, StringComparison.Ordinal);
        Assert.Contains("delegate to a **code-reviewer** for each review scope you judge it needs", body, StringComparison.Ordinal);
        Assert.Contains("**only what the remediation touched** gets re-reviewed, as one pass over all of it", body, StringComparison.Ordinal);
        Assert.Contains("6. **Then verification, once per cluster.**", body, StringComparison.Ordinal);
        Assert.Contains("delegate to **one** **tester** over it at the tier the change warrants", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectCarriesTheOwnersDatedBlockWithItsReceiptInThisRepository(string harness)
    {
        string body = QuotedBlockBody("architect", harness);

        Assert.Contains("**Review sets of related changes, not builders** (`2026-10-09`, owner, during Claustrum M4).", body, StringComparison.Ordinal);
        Assert.Contains("Too big for one reviewer to hold ⇒ split along natural seams (subsystem, slice, where the changes stop interacting)", body, StringComparison.Ordinal);
        Assert.Contains("never so fine it becomes one review per builder or per fix.", body, StringComparison.Ordinal);
        Assert.Contains("A two-line fix needs no more than the base-tier code-reviewer; a multi-slice change takes `xhigh` or `max` (sizing, below).", body, StringComparison.Ordinal);
        Assert.Contains("One tester gate per cluster. Wherever this file says *batch*, it means one cluster.", body, StringComparison.Ordinal);
        Assert.Contains("Receipt: Claustrum's own `NOTES.md` (orlodax/Claustrum #80).", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectFoldsEveryClusterIntoOneWhenTheCallerAsksForASingleReviewAndSizesEachReviewerByWhatItReads(string harness)
    {
        string body = Body("architect", harness);

        Assert.Contains("**the batch is the caller's whole assignment** — every cluster in it folded into one — not whatever one round of builders happened to produce", body, StringComparison.Ordinal);
        Assert.Contains("Size each code-reviewer against **what its review will read** — the cluster, or its share of a split one — not against the single largest builder's slice.", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheArchitectNoLongerReviewsTheBatchOrSizesAgainstTheAssembledBatch(string harness)
    {
        string body = Body("architect", harness);

        Assert.DoesNotContain("4. **Collect the whole batch.**", body, StringComparison.Ordinal);
        Assert.DoesNotContain("5. **Blind review.** With the batch complete", body, StringComparison.Ordinal);
        Assert.DoesNotContain("6. **Then verification.**", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Size the code-reviewer against the **assembled batch**", body, StringComparison.Ordinal);
        Assert.DoesNotContain("the corrected diff gets re-reviewed", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheBuilderIsToldReviewIsPerSetOfRelatedChangesAndTheTesterGateComesAfterTheWholeCluster(string harness)
    {
        string body = Body("builder", harness);

        Assert.Contains("them **per set of related changes** (`2026-10-09`), never once per slice or per fix.", body, StringComparison.Ordinal);
        Assert.Contains("by a tester the architect calls once the whole cluster of builders is in", body, StringComparison.Ordinal);
        Assert.Contains("those stages are the architect's, run once over the assembled cluster", body, StringComparison.Ordinal);
        Assert.DoesNotContain("once over the whole batch of builders", body, StringComparison.Ordinal);
        Assert.DoesNotContain("over the assembled batch", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("copilot")]
    public void TheTesterArrivesLastOncePerClusterAndCoversTheClustersBehaviour(string harness)
    {
        string body = Body("tester", harness);

        Assert.Contains("**You normally arrive last, once per cluster.**", body, StringComparison.Ordinal);
        Assert.Contains("once every builder in a cluster of related changes has reported and its code review has been triaged", body, StringComparison.Ordinal);
        Assert.Contains("Cover the cluster's behaviour as it now stands", body, StringComparison.Ordinal);
        Assert.DoesNotContain("You normally arrive last, over a whole batch", body, StringComparison.Ordinal);
    }

    // The tier stubs carry none of this text (NOTES.md "Architect role: review per cluster"), so only the base
    // goldens moved: a stub that started repeating it would silently diverge from the role it points at.
    [Theory]
    [InlineData("architect", "xhigh")]
    [InlineData("architect", "max")]
    [InlineData("builder", "xhigh")]
    [InlineData("builder", "max")]
    [InlineData("tester", "xhigh")]
    [InlineData("tester", "max")]
    public void TheTierStubsCarryNoClusterReviewText(string role, string tier)
    {
        string stub = Prose.Flatten(new RoleRenderer(library).RenderTierStub(role, tier, cwd));

        Assert.DoesNotContain("per set of related changes", stub, StringComparison.Ordinal);
        Assert.DoesNotContain("once per cluster", stub, StringComparison.Ordinal);
    }
}
