using Claustrum.Casts;
using Claustrum.Coordination;

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

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

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

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

        Assert.Contains("- builder: model claude:opus, tier high, max_parallel 3", appendix, StringComparison.Ordinal);
        Assert.Contains("- tester: model claude:haiku, tier high", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("tester: model claude:haiku, tier high, max_parallel", appendix, StringComparison.Ordinal);
    }

    // MaxParallel defaults to 1 when the role entry itself has none set (CoordinationBrief.RoleLine).
    [Fact]
    public void BuilderWithNoMaxParallelSetStillPrintsOne()
    {
        Cast cast = BuildCast(roles: new Dictionary<string, CastRoleEntry?>
        {
            ["builder"] = new CastRoleEntry(Model: null, Backend: null, Tier: null),
        });

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

        Assert.Contains("- builder: model per claustrum.json, tier high, max_parallel 1", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void ACappedCastPrintsTheBudgetLine()
    {
        Cast cast = BuildCast(budgetUsd: 12.5m);

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

        Assert.Contains("Budget: $12.50 across the whole job tree", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlimitedCastPrintsTheUnlimitedLineInstead()
    {
        Cast cast = BuildCast(budgetUsd: null);

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

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

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

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

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "my cast", "/repo with space", "job-1");

        Assert.Contains(
            "Delegate with: claustrum run <role> --cast \"my cast\" --brief-file <path> --json --cwd \"/repo with space\"",
            appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModelOverrideOmitsTheThisRunSuffix()
    {
        Cast cast = BuildCast(architect: new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"));

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1", modelOverride: null);

        Assert.Contains("- architect: mode spawned, model claude:opus, tier high — that is you, in this run", appendix, StringComparison.Ordinal);
        Assert.DoesNotContain("this run:", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelOverrideAddsTheThisRunSuffixNextToTheCastsOwnModel()
    {
        Cast cast = BuildCast(architect: new CastArchitect(CastArchitect.Spawned, Model: "claude:opus"));

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1", modelOverride: "claude:haiku");

        Assert.Contains("model claude:opus, tier high (this run: claude:haiku)", appendix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJobIdAndInspectCommandAppear()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "the-job-id");

        Assert.Contains("Job tree: the-job-id", appendix, StringComparison.Ordinal);
        Assert.Contains("Inspect: claustrum jobs budget the-job-id", appendix, StringComparison.Ordinal);
        Assert.Contains("Work branch: claustrum/the-job-id", appendix, StringComparison.Ordinal);
    }

    // The four distinct shapes a budget_exceeded error can take (DelegateEngine/BudgetLedger), all
    // named so a spawned architect knows what to do for each without re-deriving it from an error string.
    [Fact]
    public void TheFourBudgetExceededShapesAreAllNamed()
    {
        Cast cast = BuildCast();

        string appendix = CoordinationBrief.RenderSystemAppendix(cast, "default", "/repo", "job-1");

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
