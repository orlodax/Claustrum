using Claustrum.Core.Process;

namespace Claustrum.Live;

// `<job>/host.json` (#124 stage 2): the detached `claustrum jobs host` process that runs the job, written by the process that
// started it. Pid and StartedAt are how another process tells that host alive from a reused pid (ProcessIdentity).
public sealed record HostRecord(int Pid, DateTimeOffset StartedAt)
{
    public bool IsAlive() => ProcessIdentity.IsAlive(Pid, StartedAt);
}
