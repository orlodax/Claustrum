namespace Claustrum.Core.Live;

// One process for the whole conversation (claude, #124 stage 1). ReadTurnEnd is the one stdout line that
// closes a turn (null for every other line).
public interface IStreamLiveProtocol : ILiveProtocol
{
    /// <summary>One line for the child's stdin, without the line break. A <paramref name="uuid"/> is what <see cref="ReadReplay"/> names the message by.</summary>
    string EncodeUserMessage(string text, string? uuid = null);

    LiveTurn? ReadTurnEnd(string stdoutLine);

    /// <summary>The stdout line that says a message was taken (#127), null for any other line or for a backend that does not say.</summary>
    LiveReplay? ReadReplay(string stdoutLine) => null;
}
