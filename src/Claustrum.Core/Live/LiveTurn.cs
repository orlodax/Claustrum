namespace Claustrum.Core.Live;

// Text is the turn's final assistant text; IsError a turn the backend itself ended in failure (a budget
// stop, an API error), which no human reply can continue. SessionId is what a per-turn backend names for
// the next process to resume (null for a stream one, which never starts another).
public sealed record LiveTurn(string Text, bool IsError, string? SessionId = null);
