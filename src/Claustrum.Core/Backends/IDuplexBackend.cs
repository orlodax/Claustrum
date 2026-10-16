using Claustrum.Core.Jobs;
using Claustrum.Core.Live;
using Claustrum.Core.Process;

namespace Claustrum.Core.Backends;

// A backend Claustrum talks to over its process's own stdin and stdout for the whole run, live or not (ACP, #130): the
// driver it returns is what ProcessRunner feeds, in place of — and, for a live run, wrapping — the LiveSession.
// NOTES.md "The ACP backend as built (2026-10-16)" has why the driver wraps the session in its per-turn shape.
public interface IDuplexBackend
{
    IProcessLive CreateDriver(ResolvedRun run, JobPaths job, LiveSession? live);

    /// <summary>The per-turn carrier of a live session's next message: a ProcessSpec that is never launched, read back by the driver.</summary>
    ProcessSpec CarryTurn(string sessionId, string message);
}
