namespace Claustrum.Core.Tracing;

// #145: a refusal the backend listed itself (claude's result `permission_denials`): the tool, what it was called on, and the stdout.log line
// of the refused call's result (the `result` event's own line when the call is not in the stream).
public sealed record TracedDenial(string Tool, string? Command, string? Path, string? Url, int Line);
