using Claustrum.Core.Config;
using Claustrum.Core.Live;

namespace Claustrum.Core.Backends;

public interface IBackend
{
    string Name { get; }

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
}
