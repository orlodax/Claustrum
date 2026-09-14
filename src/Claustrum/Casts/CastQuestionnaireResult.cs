namespace Claustrum.Casts;

// `ExistingCasts` backs docs/PLAN.md §D2's "reuse existing cast <x>?" — surfaced as data here so the
// interviewer (a chat skill or the CLI/MCP caller) decides how to ask it, not this questionnaire.
public sealed record CastQuestionnaireResult(CastQuestion[] Questions, string[] ExistingCasts);
