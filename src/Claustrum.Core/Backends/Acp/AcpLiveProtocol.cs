using Claustrum.Core.Live;

namespace Claustrum.Core.Backends.Acp;

// An ACP live session runs on LiveSession's per-turn rules (a message sent mid-turn waits for the turn to end: opencode refuses a
// second prompt with -32603), but every turn is a `session/prompt` on the one process AcpSession drives. AcpSession hands each
// answered turn to LiveSession.TurnEnded as text (stdout), error (stderr) and 0/1 (exit code), and reads the next message back.
public sealed class AcpLiveProtocol : IResumeLiveProtocol
{
    public static readonly AcpLiveProtocol Instance = new();

    // LiveSession only needs a non-null session id to ask for a next turn, and the carrier ignores it: the ACP session itself is
    // AcpSession's, opened once per process.
    public const string SessionPlaceholder = "acp";

    public LiveTurn ReadTurn(string stdout, string stderr, int exitCode)
    {
        if (exitCode != 0)
            return new LiveTurn(stderr.Length > 0 ? stderr : "the agent ended the turn with an error", IsError: true, SessionPlaceholder);

        // A turn that ends clean but says nothing leaves a person nothing to answer (OpencodeLiveProtocol's rule).
        return stdout.Length == 0
            ? new LiveTurn("the agent ended the turn without any text", IsError: true, SessionPlaceholder)
            : new LiveTurn(stdout, IsError: false, SessionPlaceholder);
    }

    // The carrier is IProcessLive.NextTurnAsync's return type reused: a ProcessSpec that is never launched, its StdinText the
    // message. AcpSession reads it back with Message and sends it as the next `session/prompt`.
    public static ProcessSpec Carry(string message) => new("acp", [], "", [], [], StdinText: message);

    public static string Message(ProcessSpec carried) => carried.StdinText ?? "";
}
