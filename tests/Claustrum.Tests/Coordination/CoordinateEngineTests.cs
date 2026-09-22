using Claustrum.Casts;
using Claustrum.Cli;
using Claustrum.Coordination;
using Claustrum.Core.Config;

namespace Claustrum.Tests.Coordination;

// CoordinateEngine.PlanAsync touches no AppServices singleton (CastStore is plain file I/O, gh goes
// through the caller-supplied IIssueSource), so this needs neither AppServicesHomeFixture nor a real
// `gh` — a FakeIssueSource that records its calls is the whole seam.
public sealed class CoordinateEngineTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-coordinate-engine-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    private CoordinateRequest Request(int[]? issues = null, string? brief = null, int? timeoutSeconds = null) => new(
        Cwd: cwd,
        CastName: null,
        Issues: issues ?? [],
        Brief: brief,
        TierFlag: null,
        Overrides: new ConfigOverrides(TimeoutSeconds: timeoutSeconds),
        Stream: false,
        DiffCapBytes: 64 * 1024);

    [Fact]
    public async Task NeitherIssuesNorBriefThrowsUsageAndNeverCallsTheIssueSourceAsync()
    {
        FakeIssueSource issues = new();

        await Assert.ThrowsAsync<CliUsageException>(() => CoordinateEngine.PlanAsync(Request(), issues, TestContext.Current.CancellationToken));

        Assert.Empty(issues.Calls);
    }

    [Fact]
    public async Task BothIssuesAndBriefThrowsUsageAndNeverCallsTheIssueSourceAsync()
    {
        FakeIssueSource issues = new();

        await Assert.ThrowsAsync<CliUsageException>(
            () => CoordinateEngine.PlanAsync(Request(issues: [1], brief: "do it"), issues, TestContext.Current.CancellationToken));

        Assert.Empty(issues.Calls);
    }

    [Fact]
    public async Task NonPositiveTimeoutThrowsUsageAsync()
    {
        FakeIssueSource issues = new();

        CliUsageException ex = await Assert.ThrowsAsync<CliUsageException>(
            () => CoordinateEngine.PlanAsync(Request(brief: "do it", timeoutSeconds: 0), issues, TestContext.Current.CancellationToken));

        Assert.Contains("--timeout", ex.Message, StringComparison.Ordinal);
        Assert.Empty(issues.Calls);
    }

    [Fact]
    public async Task ANegativeTimeoutAlsoThrowsUsageAsync()
    {
        FakeIssueSource issues = new();

        await Assert.ThrowsAsync<CliUsageException>(
            () => CoordinateEngine.PlanAsync(Request(brief: "do it", timeoutSeconds: -5), issues, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingCastThrowsCastExceptionAsync()
    {
        FakeIssueSource issues = new();

        await Assert.ThrowsAsync<CastException>(() => CoordinateEngine.PlanAsync(Request(brief: "do it"), issues, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABriefTaskSkipsTheIssueSourceEntirelyAsync()
    {
        SaveDefaultCast();
        FakeIssueSource issues = new();

        CoordinatePlan plan = await CoordinateEngine.PlanAsync(Request(brief: "fix the thing"), issues, TestContext.Current.CancellationToken);

        Assert.Empty(issues.Calls);
        Assert.Contains("fix the thing", plan.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssuesAreFetchedInTheOrderGivenAsync()
    {
        SaveDefaultCast();
        FakeIssueSource issues = new();

        await CoordinateEngine.PlanAsync(Request(issues: [12, 3, 99]), issues, TestContext.Current.CancellationToken);

        Assert.Equal([12, 3, 99], issues.Calls.Select(call => call.Number));
        Assert.True(issues.Calls.All(call => call.Cwd == cwd));
    }

    [Fact]
    public async Task SuccessfulPlanCarriesTheLoadedCastAndItsNameAsync()
    {
        SaveDefaultCast();
        FakeIssueSource issues = new();

        CoordinatePlan plan = await CoordinateEngine.PlanAsync(Request(brief: "hi"), issues, TestContext.Current.CancellationToken);

        Assert.Equal("default", plan.CastName);
        Assert.Equal(CastArchitect.Host, plan.Cast.Architect.Mode);
    }

    private void SaveDefaultCast() =>
        CastStore.Save(cwd, new Cast("default", "1.0.0", new CastArchitect(CastArchitect.Host), [], null));

    private sealed class FakeIssueSource : IIssueSource
    {
        public List<(string Cwd, int Number)> Calls { get; } = [];

        public Task<GhIssue> ViewAsync(string cwd, int number, CancellationToken cancellationToken)
        {
            Calls.Add((cwd, number));
            return Task.FromResult(new GhIssue(number, $"issue {number}", "body", [], $"https://example.invalid/{number}"));
        }
    }
}
