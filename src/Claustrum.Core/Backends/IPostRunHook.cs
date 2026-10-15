using Claustrum.Core.Config;
using Claustrum.Core.Model;
using Claustrum.Core.Platform;

namespace Claustrum.Core.Backends;

/// <summary>
/// Optional post-run enrichment: a backend that can fetch cost/usage data after the process has
/// finished (e.g. from a session export command) implements this. Called by Runner after Parse
/// and before ChargeAsync, only when the job ran. Returning null skips enrichment entirely.
/// </summary>
public interface IPostRunHook
{
    Task<PostRunResult?> PostRunAsync(
        ParsedOutput parsed,
        string cwd,
        BackendConfig? backendConfig,
        IPlatform platform,
        CancellationToken cancellationToken);
}

public sealed record PostRunResult(decimal? CostUsd, Usage? Usage, string? Warning);
