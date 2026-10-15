using System.Text.Json.Serialization;
using Claustrum.Core.Json;
using Claustrum.Core.Process;

namespace Claustrum.Core.Live;

// `<job>/live.json` (#124), rewritten atomically on every change. QuestionSeq is the open question's
// ConversationEntry.Seq while State is WaitingForInput; ReplyTimeoutSeconds is how long the session
// waits for a reply before telling the role to decide alone (0 = never waits). OwnerPid and OwnerStartedAt
// name the claustrum process running the session (0 / null in a file written before they existed): a
// session killed without reaching its end leaves "running" behind for ever, and IsOwnerAlive is how a
// reader tells that from a live one.
public sealed record LiveState(LiveStatus State, int? QuestionSeq, DateTimeOffset Since, int ReplyTimeoutSeconds, int OwnerPid = 0, DateTimeOffset? OwnerStartedAt = null)
{
    /// <summary>
    /// False when the owner is gone, or its pid now belongs to another process (the start time differs). True when
    /// it is alive, when no owner was recorded, or when it exists but cannot be inspected: a live job is never
    /// declared dead on a guess.
    /// </summary>
    public bool IsOwnerAlive() => OwnerPid <= 0 || ProcessIdentity.IsAlive(OwnerPid, OwnerStartedAt);
}

[JsonConverter(typeof(SnakeCaseEnumConverter<LiveStatus>))]
public enum LiveStatus
{
    Running,
    WaitingForInput,
    Done,
}
