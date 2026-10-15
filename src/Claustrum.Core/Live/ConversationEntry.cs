using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Live;

// One line of `<job>/conversation.jsonl` (#124): everything said between a live role and its human, in
// order. Append-only; written by the live session, read by `jobs follow`, `jobs wait` and MCP job_status.
public sealed record ConversationEntry(int Seq, DateTimeOffset At, ConversationParty From, ConversationKind Kind, string Text);

[JsonConverter(typeof(SnakeCaseEnumConverter<ConversationParty>))]
public enum ConversationParty
{
    Role,
    Human,
    Claustrum,
}

// Question: a role turn that ended without a claustrum-report block. Reply: the human's answer to the
// open question. Message: anything else said unprompted. Notice: Claustrum itself (timeout warning,
// "no answer arrived", the blind-role refusal).
[JsonConverter(typeof(SnakeCaseEnumConverter<ConversationKind>))]
public enum ConversationKind
{
    Message,
    Question,
    Reply,
    Notice,
}
