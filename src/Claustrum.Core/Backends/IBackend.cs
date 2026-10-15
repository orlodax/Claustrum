using Claustrum.Core.Config;

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
}
