using Claustrum.Core.Config;
using Claustrum.Core.Live;

namespace Claustrum.Core.Backends;

public interface IBackend
{
    string Name { get; }

    /// <summary>
    /// The harness family the role is rendered for, and the name role.json <c>harnesses</c> lists (#130): an ACP backend returns its
    /// harness's (<c>acp-opencode</c> → <c>opencode</c>), every other backend its own name.
    /// </summary>
    string Harness => Name;

    Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken);

    ProcessSpec Build(ResolvedRun run);

    ParsedOutput Parse(string stdout, string stderr, int exitCode);

    /// <summary>
    /// Creates a stream formatter for this backend, or null for pass-through (raw lines echoed
    /// unchanged). Called once per run when <c>--stream</c> is active.
    /// </summary>
    IStreamFormatter? CreateStreamFormatter(string cwd) => null;

    /// <summary>
    /// How this backend holds a conversation (#124): messages on one process's stdin (claude) or one process per turn,
    /// the session resumed (opencode). Null when it cannot: a run that asks for a live session on such a backend
    /// simply runs as it always did.
    /// </summary>
    ILiveProtocol? LiveProtocol => null;

    /// <summary>
    /// Whether a run can be continued from the session id it returned (<c>ResolvedRun.ResumeSession</c>): what `jobs answer` needs of
    /// the backend that asked (#124 stage 3b). False for one that keeps no session between runs.
    /// </summary>
    bool CanResume => true;
}
