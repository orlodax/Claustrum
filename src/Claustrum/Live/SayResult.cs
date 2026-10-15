namespace Claustrum.Live;

// What `jobs say` / MCP job_say report once the message is queued: AnswersQuestion is the Seq of the question that was
// open when it was written (the engine decides on consumption, so this is what the sender saw, not a promise).
public sealed record SayResult(int? AnswersQuestion, string State);
