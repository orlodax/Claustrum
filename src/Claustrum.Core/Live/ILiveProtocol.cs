namespace Claustrum.Core.Live;

// How one backend speaks a conversation over its stdin and stdout (#124): the session itself knows nothing
// of the wire format. ReadTurnEnd is the one stdout line that closes a turn (null for every other line).
public interface ILiveProtocol
{
    /// <summary>One line for the child's stdin, without the line break.</summary>
    string EncodeUserMessage(string text);

    LiveTurn? ReadTurnEnd(string stdoutLine);
}

// Text is the turn's final assistant text; IsError a turn the backend itself ended in failure (a budget
// stop, an API error), which no human reply can continue.
public sealed record LiveTurn(string Text, bool IsError);
