using Claustrum.Core.Jobs;

namespace Claustrum.Core.Tests.Jobs;

public sealed class RoleConcurrencyGateTests : IDisposable
{
    // Long enough that a slow CI box never trips it, short enough that a genuinely stuck acquire
    // fails the test instead of hanging the run.
    private static readonly TimeSpan generousTimeout = TimeSpan.FromSeconds(30);

    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-gate-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public async Task AcquireSucceedsImmediatelyWhenASlotIsFreeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using RoleConcurrencyGate gate = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);

        Assert.NotNull(gate);
    }

    [Fact]
    public async Task ASecondAcquireAtCapacityOneWaitsUntilTheFirstIsDisposedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);

        Task<RoleConcurrencyGate> secondTask = RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);
        await Task.Delay(200, ct);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        RoleConcurrencyGate second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task MaxParallelTwoAllowsTwoConcurrentHoldersAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 2, generousTimeout, ct);
        await using RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 2, generousTimeout, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DifferentKeysDoNotContendForTheSameSlotAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using RoleConcurrencyGate builder = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);
        await using RoleConcurrencyGate tester = await RoleConcurrencyGate.AcquireAsync(cwd, "tester", maxParallel: 1, generousTimeout, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotNull(builder);
        Assert.NotNull(tester);
    }

    [Fact]
    public async Task DisposingFreesTheSlotForReuseAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);
        await first.DisposeAsync();

        RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await second.DisposeAsync();
    }

    // Review finding: the acquire loop used to be `while (true)`, so a sibling holding its slot (or a
    // directory that could not be written at all) hung `claustrum run` with no output and no end.
    [Fact]
    public async Task AcquireGivesUpWithATimeoutInsteadOfPollingForeverAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using RoleConcurrencyGate held = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, generousTimeout, ct);

        TimeoutException ex = await Assert.ThrowsAsync<TimeoutException>(
            () => RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, TimeSpan.FromMilliseconds(300), ct));

        Assert.Contains("builder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("jobs list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxParallelBelowOneIsRejectedRatherThanSpinningAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 0, generousTimeout, ct));
    }

    // docs/PLAN.md §D4 caps per cast. Keying the pool on the role alone let a second cast with a
    // bigger max_parallel for the same role widen the first cast's cap (review finding).
    [Fact]
    public async Task TwoCastsWithTheSameRoleDoNotShareASlotPoolAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string defaultBuilder = RoleConcurrencyGate.KeyFor("default", "builder");
        string heavyBuilder = RoleConcurrencyGate.KeyFor("heavy", "builder");

        Assert.NotEqual(defaultBuilder, heavyBuilder);

        await using RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, defaultBuilder, maxParallel: 1, generousTimeout, ct);
        await using RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, heavyBuilder, maxParallel: 1, generousTimeout, ct)
            .WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotNull(second);
    }

    [Fact]
    public void KeyForFallsBackToDefaultAndSanitizesPathCharacters()
    {
        Assert.Equal("default__builder", RoleConcurrencyGate.KeyFor(null, "builder"));
        Assert.Equal("default__builder", RoleConcurrencyGate.KeyFor("", "builder"));
        Assert.Equal("a_b__c_d", RoleConcurrencyGate.KeyFor("a/b", "c\\d"));
    }
}
