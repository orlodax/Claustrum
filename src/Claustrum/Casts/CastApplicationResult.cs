using Claustrum.Core.Config;

namespace Claustrum.Casts;

// The return type of CastApplication.Resolve (#121). The 5-ary Deconstruct matches the original
// 5-tuple callers (CoordinateEngine, pre-#121 tests) keep compiling unchanged; the 6-ary Deconstruct
// adds the cast-resolution warnings that `run`, `delegate` and `delegate_async` turn into
// result.json's `warnings[]` and stderr. New callers that want warnings use the 6-ary form.
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

    // 5-ary for existing callers (CoordinateEngine, tests) that do not yet read warnings.
    public void Deconstruct(out string tier, out ConfigOverrides overrides, out CastBudget? castBudget, out int? maxParallel, out string? castName)
    {
        tier = Tier;
        overrides = Overrides;
        castBudget = CastBudget;
        maxParallel = MaxParallel;
        castName = CastName;
    }

    // 6-ary for the callers that thread warnings into DelegateRequest (RunCommand, ClaustrumTools).
    public void Deconstruct(out string tier, out ConfigOverrides overrides, out CastBudget? castBudget, out int? maxParallel, out string? castName, out string[] warnings)
    {
        tier = Tier;
        overrides = Overrides;
        castBudget = CastBudget;
        maxParallel = MaxParallel;
        castName = CastName;
        warnings = Warnings;
    }
}
