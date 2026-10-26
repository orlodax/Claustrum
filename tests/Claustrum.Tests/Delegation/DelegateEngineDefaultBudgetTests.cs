using Claustrum.Casts;
using Claustrum.Core.Config;
using Claustrum.Core.Jobs;
using Claustrum.Core.Model;
using Claustrum.Delegation;
using Claustrum.Tests.Testing;

namespace Claustrum.Tests.Delegation;

// #137: with no cast active the per-run cap is --budget > defaults.budget_usd (built-in $5), and a config layer can now say `"budget_usd": null`:
// no default cap. DelegateEngine.Prepare reads it as config.Merged.Defaults?.BudgetUsd?.Usd and hands it to CastResolution.ApplyBudget, so
// --budget and a cast's own value (a number or null) still win; inside a job tree a null default is no tree budget and no ledger at all.
// Each test's repo claustrum.json carries the layer under test (the fixture's user config only answers the model aliases), and
// backend "nonexistent" lets RunAsync reach Runner's ledger admission without a backend installed.
[Collection(AppServicesHomeCollectionDefinition.Name)]
public sealed class DelegateEngineDefaultBudgetTests(AppServicesHomeFixture fixture) : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-default-budget-").FullName;

    // A developer's own CLAUSTRUM_BUDGET_USD must not decide what the repo layer under test resolves to.
    public void Dispose()
    {
        TempTree.Delete(cwd);
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = null;
        fixture.Platform.Environment.Remove("CLAUSTRUM_BUDGET_USD");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A `.git` entry is all Config.Load looks for before it reads claustrum.json (ClaustrumCli.MarkAsGitRoot).
    private void RepoDefaults(string? budgetJson)
    {
        fixture.Platform.Environment["CLAUSTRUM_BUDGET_USD"] = null;
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        string defaults = budgetJson is null ? "{}" : "{\"budget_usd\":" + budgetJson + "}";
        File.WriteAllText(Path.Combine(cwd, "claustrum.json"), "{\"defaults\":" + defaults + "}");
    }

    private DelegateRequest Request(decimal? flagBudget = null, CastBudget? castBudget = null) => new(
        Role: "builder",
        Brief: "hi",
        Cwd: cwd,
        Tier: "high",
        Overrides: new ConfigOverrides(Backend: "nonexistent", BudgetUsd: flagBudget),
        ResumeSession: null,
        AttachFiles: [],
        Env: [],
        Stream: false,
        DiffCapBytes: 64 * 1024,
        CastBudget: castBudget);

    private string LedgerDirectory(string treeId) => BudgetLedger.DirectoryFor(fixture.Platform, treeId);

    [Fact]
    public void ANullDefaultAndNoCastGivesTheRunNoCap()
    {
        RepoDefaults("null");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());

        Assert.Null(prepared.BudgetUsd);
        Assert.Null(prepared.Options.Tree);
    }

    [Fact]
    public void ANumericDefaultAndNoCastStillCapsTheRun()
    {
        RepoDefaults("2.5");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());

        Assert.Equal(2.5m, prepared.BudgetUsd);
    }

    [Fact]
    public void NoKeyAnywhereStillGivesTheBuiltInFiveDollarCap()
    {
        RepoDefaults(null);

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());

        Assert.Equal(5m, prepared.BudgetUsd);
    }

    [Fact]
    public void ADollarZeroDefaultIsACapOfZeroNotNoCap()
    {
        RepoDefaults("0");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());

        Assert.Equal(0m, prepared.BudgetUsd);
    }

    [Fact]
    public void AnExplicitBudgetWinsOverANullDefault()
    {
        RepoDefaults("null");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(flagBudget: 1.25m));

        Assert.Equal(1.25m, prepared.BudgetUsd);
    }

    [Fact]
    public void ACastsNumberWinsOverANullDefault()
    {
        RepoDefaults("null");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(castBudget: new CastBudget(10m)));

        Assert.Equal(10m, prepared.BudgetUsd);
    }

    [Fact]
    public void ACastsNullWinsOverANumericDefault()
    {
        RepoDefaults("3");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(castBudget: new CastBudget(null)));

        Assert.Null(prepared.BudgetUsd);
    }

    [Fact]
    public void AnExplicitBudgetWinsOverACastsNullToo()
    {
        RepoDefaults("null");

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(flagBudget: 2m, castBudget: new CastBudget(null)));

        Assert.Equal(2m, prepared.BudgetUsd);
    }

    // ---- inside a job tree (CLAUSTRUM_PARENT_JOB) ---------------------------------------------------------------------

    [Fact]
    public async Task InsideATreeANullDefaultAndNoCastMeansNoTreeBudgetAndNoLedgerAsync()
    {
        RepoDefaults("null");
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());
        RunResult result = await DelegateEngine.RunAsync(prepared, job: null, Ct);

        Assert.Null(prepared.Options.Tree);
        Assert.Null(prepared.BudgetUsd);
        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.False(Directory.Exists(LedgerDirectory(treeId)), "no tree budget means no ledger directory");
    }

    [Fact]
    public async Task InsideATreeANumericDefaultStillBuildsTheTreeBudgetAndItsLedgerAsync()
    {
        RepoDefaults("4");
        string treeId = $"tree-{Guid.NewGuid():N}";
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = treeId;

        PreparedDelegation prepared = DelegateEngine.Prepare(Request());
        RunResult result = await DelegateEngine.RunAsync(prepared, job: null, Ct);

        Assert.NotNull(prepared.Options.Tree);
        Assert.Equal(treeId, prepared.Options.Tree.TreeId);
        Assert.Equal(4m, prepared.Options.Tree.BudgetUsd);
        Assert.Null(prepared.BudgetUsd);
        Assert.Equal(RunStatus.BackendMissing, result.Status);
        Assert.True(Directory.Exists(LedgerDirectory(treeId)), "the numeric default is the tree's budget, so the ledger exists");
    }

    [Fact]
    public void InsideATreeACastsNumberStillBuildsTheTreeBudgetOverANullDefault()
    {
        RepoDefaults("null");
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = $"tree-{Guid.NewGuid():N}";

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(castBudget: new CastBudget(6m)));

        Assert.NotNull(prepared.Options.Tree);
        Assert.Equal(6m, prepared.Options.Tree.BudgetUsd);
    }

    [Fact]
    public void InsideATreeACastsNullIsNoTreeBudgetEvenOverANumericDefault()
    {
        RepoDefaults("4");
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = $"tree-{Guid.NewGuid():N}";

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(castBudget: new CastBudget(null)));

        Assert.Null(prepared.Options.Tree);
        Assert.Null(prepared.BudgetUsd);
    }

    [Fact]
    public void InsideATreeAnExplicitBudgetOverANullDefaultIsThePerRunCapWithNoTree()
    {
        RepoDefaults("null");
        fixture.Platform.Environment["CLAUSTRUM_PARENT_JOB"] = $"tree-{Guid.NewGuid():N}";

        PreparedDelegation prepared = DelegateEngine.Prepare(Request(flagBudget: 1.5m));

        Assert.Null(prepared.Options.Tree);
        Assert.Equal(1.5m, prepared.BudgetUsd);
    }
}
