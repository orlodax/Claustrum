using Claustrum.Core.Backends;

namespace Claustrum.Core.Process;

// What ProcessRunner needs from a live session (#124, Claustrum.Core.Live), kept here so Process/ does not depend on Live/: Attach hands
// over the clock before any output is read, OnStdoutLine sees every stdout line, RunAsync owns stdin from the first message until it closes
// it, and Ended is called once the process is gone, whatever ended it. ReportDelivered: the report was recorded, so a later stop (a timeout,
// a cancel) does not change the outcome. PerTurn: the backend resumes its session per turn (IResumeLiveProtocol), so each turn is its own
// process; the runner reads no stdout lines and gives RunAsync no stdin, and after each process calls TurnEnded, then NextTurnAsync (null:
// the session is over). NextTurnFailed: that next turn could not be started, so the session ends there and the runner ends the job failed.
public interface IProcessLive
{
    bool ReportDelivered { get; }

    bool PerTurn { get; }

    void Attach(PausableDeadline deadline);

    void OnStdoutLine(string line);

    Task RunAsync(TextWriter? stdin, CancellationToken processEnded);

    void TurnEnded(int exitCode, string stdout, string stderr);

    Task<ProcessSpec?> NextTurnAsync(CancellationToken cancellationToken);

    void NextTurnFailed(string reason);

    void Ended();
}
