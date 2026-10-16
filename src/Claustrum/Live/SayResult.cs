namespace Claustrum.Live;

// What `jobs say` / MCP job_say report once the message is queued: AnswersQuestion is the Seq of the question that was
// open when it was written (the engine decides on consumption, so this is what the sender saw, not a promise). RetriedAs is the job that
// took the message when the one named had been retried on its cast's fallback (#126), null otherwise.
public sealed record SayResult(int? AnswersQuestion, string State, string? RetriedAs = null);
