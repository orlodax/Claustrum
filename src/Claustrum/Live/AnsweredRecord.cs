namespace Claustrum.Live;

// `<job>/answered.json` (#124 stage 3 review F2): the continuation that took a `needs_input` job's answer, written once and never replaced.
public sealed record AnsweredRecord(string JobId, DateTimeOffset At);
