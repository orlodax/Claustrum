using System.Text.Json;

namespace Claustrum.Live;

// `jobs wait --json`'s one document (#124). State is running | waiting_for_input | done; Question is set only while
// waiting_for_input; ResultStatus is the receipt's `status` once result.json is readable, else null (also when a
// done job's receipt never became readable). EnvironmentIssues is the receipt's own `environment_issues` array, as written (#145).
public sealed record JobWaitResult(string State, QuestionInfo? Question, string? ResultStatus, JsonElement? EnvironmentIssues = null);
