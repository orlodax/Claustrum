namespace Claustrum.Casts;

// `ExistingCasts` backs docs/PLAN.md §D2's "reuse existing cast <x>?" — surfaced as data here so the
// interviewer (a chat skill or the CLI/MCP caller) decides how to ask it, not this questionnaire.
// `Models` (#121) shows every alias in claustrum.json and what it resolves to, so questions are seen
// alongside the model table.
public sealed record CastQuestionnaireResult(
    CastQuestion[] Questions,
    string[] ExistingCasts,
    CastModelOption[]? Models = null);
