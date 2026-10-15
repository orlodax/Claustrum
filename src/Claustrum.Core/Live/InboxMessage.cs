using System.Text.Json.Serialization;

namespace Claustrum.Core.Live;

// `<job>/inbox/<ticks>-<random>.json` (#124): one message from the human to a live role, written
// atomically (temp file + rename) by `jobs say` / MCP job_say and consumed, then deleted, by the live
// session, which records it in conversation.jsonl as a Reply (a question was open) or a Message.
// Notice is a Claustrum process speaking instead (a detached host whose tree ended, #124 stage 2 review F1): the session records
// it as a Notice and sends the role nothing. Left out of the file when false, so a human message reads as it always did.
public sealed record InboxMessage(DateTimeOffset At, string Text, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Notice = false);
