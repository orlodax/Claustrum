using Claustrum.Core.Model;

namespace Claustrum.Cli;

// Line is what `backends doctor --probe` prints after "probe:   "; Result is the run behind it, or
// null when no call was made (no alias resolved to the backend). Carrying both keeps the formatting
// assertable without a second paid round trip.
internal sealed record ProbeOutcome(string Line, RunResult? Result);
