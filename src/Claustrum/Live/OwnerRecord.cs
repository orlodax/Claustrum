using Claustrum.Core.Process;

namespace Claustrum.Live;

// `<job>/owner.json` (#124 stage 2 review F1): the process that runs a job it runs itself and that detached jobs hang from. Only
// `coordinate` writes one, into its own job directory, because a coordinate job without a live session has no live.json to name its
// owner and is no detached job with a host.json. The detached children of its tree read it to know when their root is gone.
public sealed record OwnerRecord(int Pid, DateTimeOffset StartedAt)
{
    public bool IsAlive() => ProcessIdentity.IsAlive(Pid, StartedAt);

    public static OwnerRecord ForCurrentProcess()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
        return new OwnerRecord(Environment.ProcessId, new DateTimeOffset(self.StartTime));
    }
}
