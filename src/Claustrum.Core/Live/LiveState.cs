using System.Text.Json.Serialization;
using Claustrum.Core.Json;

namespace Claustrum.Core.Live;

// `<job>/live.json` (#124), rewritten atomically on every change. QuestionSeq is the open question's
// ConversationEntry.Seq while State is WaitingForInput; ReplyTimeoutSeconds is how long the session
// waits for a reply before telling the role to decide alone (0 = never waits).
public sealed record LiveState(LiveStatus State, int? QuestionSeq, DateTimeOffset Since, int ReplyTimeoutSeconds);

[JsonConverter(typeof(SnakeCaseEnumConverter<LiveStatus>))]
public enum LiveStatus
{
    Running,
    WaitingForInput,
    Done,
}
