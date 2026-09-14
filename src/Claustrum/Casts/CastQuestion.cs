namespace Claustrum.Casts;

// docs/PLAN.md §D2: one question per cast role plus budget, with "live options" — only backends
// `doctor` finds installed, the aliases in claustrum.json, a free-form `backend:model` escape, and
// "not needed" for every role but builder.
public sealed record CastQuestion(string Key, string Prompt, string[] Options, bool AllowNotNeeded, bool AllowFreeForm);
