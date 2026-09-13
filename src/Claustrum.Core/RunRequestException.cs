namespace Claustrum.Core;

// Pre-spawn validation of a RunRequest (docs/PLAN.md A5): a bad --timeout or a missing --file path
// is a usage error, not a backend failure, so it throws before Runner touches a process — same
// pattern as BlindGateException/ConfigException, which the CLI already maps to exit code 2.
public sealed class RunRequestException(string message) : Exception(message);
