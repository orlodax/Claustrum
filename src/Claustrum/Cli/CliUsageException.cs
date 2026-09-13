namespace Claustrum.Cli;

// CLI-level input problems that map to exit code 2 (docs/PLAN.md §A5) before any RunRequest exists —
// kept separate from Core's own ConfigException/BlindGateException, which the CLI maps the same way.
public sealed class CliUsageException(string message) : Exception(message);
