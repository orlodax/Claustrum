namespace Claustrum.Core.Live;

// `<job>/inbox/<ticks>-<random>.json` (#124): one message from the human to a live role, written
// atomically (temp file + rename) by `jobs say` / MCP job_say and consumed, then deleted, by the live
// session, which records it in conversation.jsonl as a Reply (a question was open) or a Message.
public sealed record InboxMessage(DateTimeOffset At, string Text);
