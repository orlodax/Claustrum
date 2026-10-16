namespace Claustrum.Core.Live;

// A message the backend says it took off its input (claude's `--replay-user-messages` echo, #127). Uuid is the one the message was
// written with; null when the echo carries none (the brief of a session this process did not number, or a replayed history).
public sealed record LiveReplay(string? Uuid);
