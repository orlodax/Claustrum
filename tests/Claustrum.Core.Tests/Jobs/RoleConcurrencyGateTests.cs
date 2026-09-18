using Claustrum.Core.Jobs;

namespace Claustrum.Core.Tests.Jobs;

public sealed class RoleConcurrencyGateTests : IDisposable
{
    private readonly string cwd = Directory.CreateTempSubdirectory("claustrum-gate-").FullName;

    public void Dispose() => Directory.Delete(cwd, recursive: true);

    [Fact]
    public async Task AcquireSucceedsImmediatelyWhenASlotIsFreeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using RoleConcurrencyGate gate = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct);

        Assert.NotNull(gate);
    }

    [Fact]
    public async Task ASecondAcquireAtCapacityOneWaitsUntilTheFirstIsDisposedAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct);

        Task<RoleConcurrencyGate> secondTask = RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct);
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

        await using RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 2, ct);
        await using RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 2, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DifferentKeysDoNotContendForTheSameSlotAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using RoleConcurrencyGate builder = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct);
        await using RoleConcurrencyGate tester = await RoleConcurrencyGate.AcquireAsync(cwd, "tester", maxParallel: 1, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotNull(builder);
        Assert.NotNull(tester);
    }

    [Fact]
    public async Task DisposingFreesTheSlotForReuseAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct);
        await first.DisposeAsync();

        RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", maxParallel: 1, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        await second.DisposeAsync();
    }
}
