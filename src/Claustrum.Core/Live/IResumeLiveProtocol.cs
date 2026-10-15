namespace Claustrum.Core.Live;

// One process per turn (opencode, #124 stage 3a): a turn is a process that exits, and the next one resumes
// the backend's session with the message in place of the brief. ReadTurn reads one turn's whole stdout,
// finished or cut short; a non-zero exit is the turn's own error.
public interface IResumeLiveProtocol : ILiveProtocol
{
    LiveTurn ReadTurn(string stdout, string stderr, int exitCode);
}
