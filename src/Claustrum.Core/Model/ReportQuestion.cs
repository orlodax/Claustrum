namespace Claustrum.Core.Model;

// One question a role puts to its human when it stops with `needs_input` (#124 stage 3b, docs/MANUAL.md "When nobody can
// answer live"): the cast questionnaire's CastQuestion shape, without `AllowNotNeeded`, which only a role-picking question has.
public sealed record ReportQuestion(string Key, string Prompt, string[] Options, bool AllowFreeForm);
