using Claustrum.Core.Live;

namespace Claustrum.Core.Backends.Opencode;

// opencode holds no conversation on stdin: `opencode run` ends with its turn, and the next one is another
// `run --session <id>` (NOTES.md "Live sessions on opencode"). A turn is read exactly as a non-live run is,
// so its text, its error and its session id are what Parse already says; the one addition is that a turn
// that ends clean but says nothing is an error, because there is nothing for a person to answer.
public sealed class OpencodeLiveProtocol(OpencodeBackend backend) : IResumeLiveProtocol
{
    public LiveTurn ReadTurn(string stdout, string stderr, int exitCode)
    {
        ParsedOutput parsed = backend.Parse(stdout, stderr, exitCode);
        return parsed is { IsError: false, FinalMessage.Length: 0 }
            ? new LiveTurn("opencode ended the turn without any text.", IsError: true, parsed.SessionId)
            : new LiveTurn(parsed.FinalMessage, parsed.IsError, parsed.SessionId);
    }
}
