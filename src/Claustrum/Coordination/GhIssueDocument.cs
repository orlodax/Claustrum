namespace Claustrum.Coordination;

// `gh issue view <n> --json number,title,body,labels,url` verbatim, so the mapping to GhIssue is one
// visible step. Every field is nullable on purpose: the source generator does not enforce
// non-nullable reference types at runtime (CastStore.Load carries the same finding), and gh omits
// nothing today but is not this repo's code.
internal sealed record GhIssueDocument(int Number, string? Title, string? Body, GhIssueLabel[]? Labels, string? Url);

// gh nests labels as objects; only the name reaches a brief.
internal sealed record GhIssueLabel(string? Name);
