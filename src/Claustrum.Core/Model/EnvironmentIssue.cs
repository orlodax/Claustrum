namespace Claustrum.Core.Model;

// #145: one kind of trouble, grouped. Where is the path, command or host it hit (several joined, capped); First is where it was seen
// first: `stdout.log:<line>`, `report`, or `dispatch` for what the caller found before the job started.
public sealed record EnvironmentIssue(EnvironmentIssueKind Kind, string What, string? Where, int Count, string First);
