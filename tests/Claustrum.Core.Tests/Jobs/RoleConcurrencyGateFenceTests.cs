using Claustrum.Core.Jobs;
using Claustrum.Core.Tests.Testing;

namespace Claustrum.Core.Tests.Jobs;

// #104: the caller that owns the fence is asked about every path before the gate creates it, and again on every poll, so a link
// that appears during a wait is caught on the next turn. The gate itself knows no links: the callback may throw.
public sealed class RoleConcurrencyGateFenceTests : IDisposable
{
    private static readonly TimeSpan generousTimeout = TimeSpan.FromSeconds(30);

    private readonly ScratchRoot scratch = new("claustrum-gate-fence-");

    public void Dispose() => scratch.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeCreateSeesTheLockDirectoryThenEachSlotPathTriedAsync()
    {
        string cwd = scratch.CreateDirectory();
        List<string> seen = [];
        await using RoleConcurrencyGate first = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", 2, generousTimeout, Ct);

        await using RoleConcurrencyGate second = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", 2, generousTimeout, Ct, seen.Add);

        Assert.Equal(
            [RoleConcurrencyGate.LockDirectory(cwd), RoleConcurrencyGate.SlotPath(cwd, "builder", 0), RoleConcurrencyGate.SlotPath(cwd, "builder", 1)],
            seen);
    }

    [Fact]
    public async Task ACallbackThatThrowsOnTheDirectoryCreatesNothingAsync()
    {
        string cwd = scratch.CreateDirectory();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoleConcurrencyGate.AcquireAsync(cwd, "builder", 1, generousTimeout, Ct, _ => throw new InvalidOperationException("refused")));

        Assert.False(Directory.Exists(Path.Combine(cwd, ".claustrum")));
    }

    [Fact]
    public async Task ACallbackThatThrowsOnTheSlotPathCreatesNoSlotFileAsync()
    {
        string cwd = scratch.CreateDirectory();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoleConcurrencyGate.AcquireAsync(cwd, "builder", 1, generousTimeout, Ct, path =>
            {
                if (path.EndsWith(".lock", StringComparison.Ordinal))
                    throw new InvalidOperationException("refused");
            }));

        Assert.Empty(Directory.GetFiles(RoleConcurrencyGate.LockDirectory(cwd)));
    }

    // The fence is re-asked after every wait: the first poll passes, the slot is taken, and the next one throws.
    [Fact]
    public async Task AFenceThatTurnsRefusingDuringTheWaitIsAskedAgainOnTheNextPollAsync()
    {
        string cwd = scratch.CreateDirectory();
        await using RoleConcurrencyGate held = await RoleConcurrencyGate.AcquireAsync(cwd, "builder", 1, generousTimeout, Ct);
        int slotAsks = 0;
        bool flipped = false;

        Task<RoleConcurrencyGate> waiting = RoleConcurrencyGate.AcquireAsync(cwd, "builder", 1, generousTimeout, Ct, path =>
        {
            if (!path.EndsWith(".lock", StringComparison.Ordinal))
                return;

            slotAsks++;
            if (Volatile.Read(ref flipped))
                throw new InvalidOperationException("a link appeared during the wait");
        });
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.False(waiting.IsCompleted);
        Volatile.Write(ref flipped, true);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);

        Assert.Contains("appeared during the wait", ex.Message, StringComparison.Ordinal);
        Assert.True(slotAsks >= 2, $"the slot path was asked only {slotAsks} time(s)");
    }
}
