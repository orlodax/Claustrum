using Claustrum.Core.Config;

namespace Claustrum.Core.Backends;

public interface IBackend
{
    string Name { get; }

    Task<Doctor> DetectAsync(BackendConfig? config, CancellationToken cancellationToken);

    ProcessSpec Build(ResolvedRun run);

    ParsedOutput Parse(string stdout, string stderr, int exitCode);
}
