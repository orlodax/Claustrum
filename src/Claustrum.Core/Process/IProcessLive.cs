namespace Claustrum.Core.Process;

// What ProcessRunner needs from a live session (#124, Claustrum.Core.Live), kept here so Process/ does not
// depend on Live/: Attach hands over the clock before any output is read, OnStdoutLine sees every stdout
// line, RunAsync owns stdin from the moment the first message is written until it closes it, and Ended is
// called once the process is gone, whatever ended it. ReportDelivered: the role's report was recorded, so a
// stop after that (a timeout, a cancel) does not change the job's outcome.
public interface IProcessLive
{
    bool ReportDelivered { get; }

    void Attach(PausableDeadline deadline);

    void OnStdoutLine(string line);

    Task RunAsync(TextWriter stdin, CancellationToken processEnded);

    void Ended();
}
