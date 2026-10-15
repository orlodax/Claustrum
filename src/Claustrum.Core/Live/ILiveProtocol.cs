namespace Claustrum.Core.Live;

// How one backend holds a conversation (#124), in one of two shapes the session knows how to drive:
// IStreamLiveProtocol (one process, messages on its stdin) or IResumeLiveProtocol (one process per turn,
// the session resumed). The session itself knows nothing of either wire format; NOTES.md "Live sessions"
// and "Live sessions on opencode" have the rules.
public interface ILiveProtocol;
