namespace Claustrum.Core.Live;

// One process for the whole conversation (claude, #124 stage 1). ReadTurnEnd is the one stdout line that
// closes a turn (null for every other line).
public interface IStreamLiveProtocol : ILiveProtocol
{
    /// <summary>One line for the child's stdin, without the line break.</summary>
    string EncodeUserMessage(string text);

    LiveTurn? ReadTurnEnd(string stdoutLine);
}
