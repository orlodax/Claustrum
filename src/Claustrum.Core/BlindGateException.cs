namespace Claustrum.Core;

// NOTES.md "Blind review is enforced, not requested" — the runner refuses instead of trusting the
// architect's prompt discipline. The CLI layer maps this to exit code 2 (docs/PLAN.md A5).
public sealed class BlindGateException(string message) : Exception(message);
