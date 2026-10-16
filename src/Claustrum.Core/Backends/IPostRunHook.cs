using Claustrum.Core.Config;
using Claustrum.Core.Model;

namespace Claustrum.Core.Backends;

/// <summary>
/// Optional run enrichment: a backend that can fetch cost/usage from outside its own stream (e.g. a
/// session export command, or the receipts of an earlier run of the session) implements this. Runner calls
/// <see cref="PreRunAsync"/> before the process starts and <see cref="PostRunAsync"/> after Parse and before ChargeAsync;
/// for a cancelled job the latter gets 5 s and a result that carries a warning is discarded (#127). Neither may change
/// the run's status: Runner catches whatever they throw.
/// </summary>
public interface IPostRunHook
{
    /// <summary>
    /// The baseline to subtract when the run continues an existing session whose totals the backend
    /// reports cumulatively. Null when there is nothing to measure (a fresh session).
    /// </summary>
    Task<PreRunBaseline?> PreRunAsync(PostRunContext context, CancellationToken cancellationToken);

    /// <summary>Returning null skips enrichment entirely.</summary>
    Task<PostRunResult?> PostRunAsync(
        ParsedOutput parsed,
        PostRunContext context,
        PreRunBaseline? baseline,
        CancellationToken cancellationToken);
}

// Spec is what the run itself was started with (cwd, and the env with caller `--env` entries); Stdout is
// the run's whole output for a hook that needs more than Parse kept (empty at PreRunAsync time).
public sealed record PostRunContext(
    ProcessSpec Spec,
    string? ResumeSession,
    string JobDirectory,
    BackendConfig? BackendConfig,
    bool EnvPassthroughAll,
    string Stdout);

// Failure says why the baseline could not be read; the figures are null then.
public sealed record PreRunBaseline(decimal? CostUsd, Usage? Usage, string? Failure);

public sealed record PostRunResult(decimal? CostUsd, Usage? Usage, string? Warning);
