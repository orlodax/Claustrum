namespace Claustrum.Coordination;

// One GitHub issue as `coordinate --issues` needs it (docs/PLAN.md §D3): the fields
// `gh issue view <n> --json number,title,body,labels,url` returns, already flattened — labels are
// names here, not the objects gh nests them in (GhIssueDocument).
public sealed record GhIssue(int Number, string Title, string Body, string[] Labels, string Url);
