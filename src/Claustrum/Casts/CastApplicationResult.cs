using Claustrum.Core.Config;

namespace Claustrum.Casts;

// The return type of CastApplication.Resolve (#121). Callers read named properties directly.
public sealed record CastApplicationResult
{
    public required string Tier { get; init; }
    public required ConfigOverrides Overrides { get; init; }
    public CastBudget? CastBudget { get; init; }
    public int? MaxParallel { get; init; }
    public string? CastName { get; init; }

    // #121's cast-resolution warnings (built-in fallback, stored→current drift) for the one role
    // CastApplication.Resolve was asked about — empty when the model came from a flag instead of the
    // cast, when the role has no cast entry, or when no warning applies.
    public string[] Warnings { get; init; } = [];
}
