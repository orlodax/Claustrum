using Claustrum.Core.Process;

namespace Claustrum.Core.Tests.Process;

// The pid-plus-start-time check shared by live.json's owner and a detached job's host.json and owner.json (#124).
public sealed class ProcessIdentityTests
{
    private static DateTimeOffset StartOfThisProcess()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        return new DateTimeOffset(self.StartTime);
    }

    [Fact]
    public void ARunningProcessWithItsRecordedStartTimeIsAlive() =>
        Assert.True(ProcessIdentity.IsAlive(Environment.ProcessId, StartOfThisProcess()));

    [Fact]
    public void AProcessWithNoRecordedStartTimeIsAliveWhileItRuns() =>
        Assert.True(ProcessIdentity.IsAlive(Environment.ProcessId, null));

    [Fact]
    public void ARecycledPidIsNotTheRecordedProcess() =>
        Assert.False(ProcessIdentity.IsAlive(Environment.ProcessId, StartOfThisProcess().AddHours(-1)));

    [Fact]
    public void AProcessThatDoesNotExistIsNotAlive() =>
        Assert.False(ProcessIdentity.IsAlive(int.MaxValue - 1, null));
}
