using Claustrum.Core.Live;

namespace Claustrum.Core.Tests.Process;

// Review F4 on #124: live.json names its owner, and IsOwnerAlive is how a front door tells a live job from one whose
// claustrum process was killed without reaching its end.
public sealed class LiveStateOwnerTests
{
    private static LiveState Owned(int pid, DateTimeOffset? startedAt) =>
        new(LiveStatus.Running, null, DateTimeOffset.UtcNow, 0, pid, startedAt);

    [Fact]
    public void TheRunningProcessIsAliveWithItsOwnStartTime()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.True(Owned(Environment.ProcessId, self.StartTime.ToUniversalTime()).IsOwnerAlive());
    }

    [Fact]
    public void ADeadPidIsNotAlive()
    {
        System.Diagnostics.ProcessStartInfo info = OperatingSystem.IsWindows() ? new("cmd", ["/c", "exit 0"]) : new("sh", ["-c", "exit 0"]);
        info.CreateNoWindow = true;
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("no child");
        DateTimeOffset started = child.StartTime.ToUniversalTime();
        child.WaitForExit();

        Assert.False(Owned(child.Id, started).IsOwnerAlive());
    }

    [Fact]
    public void AReusedPidWithAnotherStartTimeIsNotAlive()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.False(Owned(Environment.ProcessId, self.StartTime.ToUniversalTime().AddHours(-1)).IsOwnerAlive());
    }

    [Fact]
    public void NoOwnerRecordedCountsAsAlive()
    {
        Assert.True(Owned(0, null).IsOwnerAlive());
        Assert.True(Owned(Environment.ProcessId, null).IsOwnerAlive());
    }
}
