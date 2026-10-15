namespace Claustrum.Live;

// `jobs wait --json`'s one document (#124). State is running | waiting_for_input | done; Question is set only while
// waiting_for_input; ResultStatus is the receipt's `status` once result.json is readable, else null (also when a
// done job's receipt never became readable).
public sealed record JobWaitResult(string State, QuestionInfo? Question, string? ResultStatus);
