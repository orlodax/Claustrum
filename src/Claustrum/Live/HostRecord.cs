using Claustrum.Core.Process;

namespace Claustrum.Live;

// `<job>/host.json` (#124 stage 2): the detached `claustrum jobs host` process that runs the job, written by the process that
// started it. Pid and StartedAt are how another process tells that host alive from a reused pid (ProcessIdentity). Tree is the
// CLAUSTRUM_PARENT_JOB the starter ran with: how `coordinate` finds the detached jobs of its tree when it ends, in the store and
// not in memory (review of #124 stage 2, F1). Null outside a tree, and in a file written before the field existed.
public sealed record HostRecord(int Pid, DateTimeOffset StartedAt, string? Tree = null)
{
    public bool IsAlive() => ProcessIdentity.IsAlive(Pid, StartedAt);
}
